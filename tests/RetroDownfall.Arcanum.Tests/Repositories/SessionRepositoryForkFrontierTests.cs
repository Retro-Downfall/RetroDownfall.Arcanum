using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class SessionRepositoryForkFrontierTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _databasePath = string.Empty;

    private ArcanumDbContext _database = null!;

    public Task InitializeAsync()
    {
        _databasePath = fixture.CopyDatabase();

        _database = fixture.CreateContext(_databasePath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();

        File.Delete(_databasePath);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Fork_preserves_the_immutable_binding_not_mutable_navigation(int kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        SessionRepository repository = Repository();
        Session source = await repository.CreateAsync(Guid.NewGuid(), "source", CancellationToken.None);
        Guid? campaign = kind == 2 ? Guid.NewGuid() : null;
        await SeedBindingAsync(source.Id, kind, campaign);
        Result<Session> result = await repository.ForkAsync(source.Id, new ForkSessionRequest(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error.Message);
        await using DbCommand read = _database.Database.GetDbConnection().CreateCommand();
        read.CommandText = "SELECT BindingKindCode, CampaignId FROM session_campaign_bindings WHERE SessionId = $id;";
        DbParameter id = read.CreateParameter();
        id.ParameterName = "$id";
        id.Value = result.Value.Id.ToString("D").ToUpperInvariant();
        read.Parameters.Add(id);
        await using DbDataReader row = await read.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(kind, row.GetInt32(0));
        Assert.Equal(campaign, row.IsDBNull(1) ? null : Guid.Parse(row.GetString(1)));
        Assert.Equal(campaign, result.Value.CampaignId);
    }

    [SkippableFact]
    public async Task Fork_refuses_a_source_without_an_immutable_binding()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        SessionRepository repository = Repository();
        Session source = await repository.CreateAsync(null, "missing binding", CancellationToken.None);
        Result<Session> result = await repository.ForkAsync(source.Id, new ForkSessionRequest(), CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
    }

    [SkippableFact]
    public async Task Fork_refuses_corrupt_binding_instead_of_treating_it_as_global()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        SessionRepository repository = Repository();
        Session source = await repository.CreateAsync(null, "corrupt", CancellationToken.None);
        await _database.Database.OpenConnectionAsync();
        await using DbCommand pragma = _database.Database.GetDbConnection().CreateCommand();
        pragma.CommandText = "PRAGMA ignore_check_constraints = ON;";
        await pragma.ExecuteNonQueryAsync();
        try
        {
            await SeedBindingAsync(source.Id, 99, null);
        }
        finally
        {
            pragma.CommandText = "PRAGMA ignore_check_constraints = OFF;";
            await pragma.ExecuteNonQueryAsync();
        }
        Result<Session> result = await repository.ForkAsync(source.Id, new ForkSessionRequest(), CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
    }

    [SkippableFact]
    public async Task Fork_rolls_back_the_new_binding_with_the_branch_when_entry_copy_fails()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        SessionRepository repository = Repository();
        Session source = await repository.CreateAsync(null, "rollback", CancellationToken.None);
        await SeedBindingAsync(source.Id, 1, null);
        await AddAsync(repository, source.Id, "source entry");
        await using DbCommand command = _database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "CREATE TEMP TRIGGER reject_fork_entries BEFORE INSERT ON Entries BEGIN SELECT RAISE(ABORT, 'reject entry copy'); END;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => repository.ForkAsync(source.Id, new ForkSessionRequest(), CancellationToken.None));
        command.CommandText = "SELECT count(*) FROM session_campaign_bindings;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT count(*) FROM Sessions;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private async Task SeedBindingAsync(Guid sessionId, int kind, Guid? campaign)
    {
        await _database.Database.OpenConnectionAsync();
        SqliteConnection connection = (SqliteConnection)_database.Database.GetDbConnection();
        using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session_campaign_bindings(SessionId, BindingKindCode, CampaignId, BoundAtUtc) VALUES($session, $kind, $campaign, $now);";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D").ToUpperInvariant());
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$campaign", campaign is { } value ? value.ToString("D").ToUpperInvariant() : DBNull.Value);
        command.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.0000000Z");
        await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task A_cutoff_fork_records_the_exact_copied_sequence_in_its_transaction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = Repository();

        Session source = await repository.CreateAsync(null, "source", CancellationToken.None);

        await SessionBindingWriters.BackfillExistingAsync(_database);

        Entry first = await AddAsync(repository, source.Id, "first");

        Entry second = await AddAsync(repository, source.Id, "second");

        _ = await AddAsync(repository, source.Id, "later");

        Result<Session> result = await repository.ForkAsync(source.Id,
            new ForkSessionRequest(UpToEntryId: second.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        (long sequence, int proof, string parent) = await FrontierAsync(result.Value.Id);

        Assert.Equal(second.Sequence, sequence);

        Assert.Equal(1, proof);

        Assert.Equal(source.Id.ToString("D").ToUpperInvariant(), parent);

        Assert.Equal(2, (await repository.GetEntriesAsync(result.Value.Id, limit: 10, ct: CancellationToken.None)).Count);

        Assert.True(first.Sequence < sequence);
    }

    [SkippableFact]
    public async Task A_nested_fork_excludes_its_entire_copied_prefix_including_the_parents_native_tail()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = Repository();

        Session source = await repository.CreateAsync(null, "source", CancellationToken.None);

        await SessionBindingWriters.BackfillExistingAsync(_database);

        _ = await AddAsync(repository, source.Id, "root");

        Result<Session> child = await repository.ForkAsync(source.Id, new ForkSessionRequest(), CancellationToken.None);

        Assert.True(child.IsSuccess);

        Entry tail = await AddAsync(repository, child.Value.Id, "child native");

        Result<Session> grandchild = await repository.ForkAsync(child.Value.Id, new ForkSessionRequest(), CancellationToken.None);

        Assert.True(grandchild.IsSuccess);

        Assert.Equal(tail.Sequence, (await FrontierAsync(grandchild.Value.Id)).Sequence);

        Assert.Equal(1, (await FrontierAsync(child.Value.Id)).Sequence);
    }

    [SkippableFact]
    public async Task An_empty_fork_records_a_proven_zero_frontier()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = Repository();

        Session source = await repository.CreateAsync(null, "empty", CancellationToken.None);

        await SessionBindingWriters.BackfillExistingAsync(_database);

        Result<Session> fork = await repository.ForkAsync(source.Id, new ForkSessionRequest(), CancellationToken.None);

        Assert.True(fork.IsSuccess);

        Assert.Equal(0, (await FrontierAsync(fork.Value.Id)).Sequence);

        Assert.Equal(1, (await FrontierAsync(fork.Value.Id)).Proof);
    }

    private SessionRepository Repository() => new(_database, new NoOpSessionAttachmentStore(),
        fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_database));

    private static async Task<Entry> AddAsync(SessionRepository repository, Guid sessionId, string content)
    {
        Result<Entry> result = await repository.AddEntryAsync(sessionId, new Entry
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
            ModelUsed = "test-model",
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);

        return result.Value;
    }

    private async Task<(long Sequence, int Proof, string Parent)> FrontierAsync(Guid sessionId)
    {
        await _database.Database.OpenConnectionAsync(CancellationToken.None);

        await using DbCommand command = _database.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT InheritedThroughSequence, ProofKindCode, SourceSessionId FROM campaign_fork_frontiers WHERE SessionId = $id;";

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "$id";

        parameter.Value = sessionId.ToString("D").ToUpperInvariant();

        _ = command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None), "A fork must commit its inherited frontier beside its copied Entries.");

        return (reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2));
    }
}
