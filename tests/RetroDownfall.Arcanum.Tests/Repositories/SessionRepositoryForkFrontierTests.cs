using System.Data.Common;

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

    [SkippableFact]
    public async Task A_cutoff_fork_records_the_exact_copied_sequence_in_its_transaction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = Repository();

        Session source = await repository.CreateAsync(null, "source", CancellationToken.None);

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
