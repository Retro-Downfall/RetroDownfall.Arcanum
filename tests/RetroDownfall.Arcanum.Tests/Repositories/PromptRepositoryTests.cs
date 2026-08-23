using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class PromptRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public PromptRepositoryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task AddAsync_GetByNameAndVersionAsync_and_GetByIdAsync_round_trip()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Prompt prompt = new()
        {
            Id = Guid.NewGuid(),
            Name = "summon",
            Version = "1.0.0",
            Template = "Hello {{name}}",
            Tags = PromptRepository.SerializeTags(["forge", "greeting"]),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Prompt saved = await repository.AddAsync(prompt, CancellationToken.None);

        Prompt? byId = await repository.GetByIdAsync(saved.Id, CancellationToken.None);

        Prompt? byName = await repository.GetByNameAndVersionAsync("summon", "1.0.0", campaignId: null, CancellationToken.None);

        Assert.NotNull(byId);

        Assert.NotNull(byName);

        Assert.Equal(saved.Id, byId!.Id);

        Assert.Equal(saved.Id, byName!.Id);

        Assert.Equal(["forge", "greeting"], PromptRepository.DeserializeTags(byName.Tags));

    }

    [SkippableFact]
    public async Task UpdateAsync_and_DeleteAsync_manage_prompts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Prompt alpha = await repository.AddAsync(
            new Prompt
            {
                Id = Guid.NewGuid(),
                Name = "alpha",
                Version = "1",
                Template = "A",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Prompt beta = await repository.AddAsync(
            new Prompt
            {
                Id = Guid.NewGuid(),
                Name = "beta",
                Version = "1",
                Template = "B",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        alpha.Template = "A revised";

        await repository.UpdateAsync(alpha, CancellationToken.None);

        Prompt? updated = await repository.GetByIdAsync(alpha.Id, CancellationToken.None);

        Assert.Equal("A revised", updated!.Template);

        bool deleted = await repository.DeleteAsync(beta.Id, CancellationToken.None);

        Assert.True(deleted);

        Assert.Null(await repository.GetByIdAsync(beta.Id, CancellationToken.None));

    }

    [SkippableFact]
    public async Task AddAsync_retries_real_sqlite_lock_contention()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _db!.Database.OpenConnectionAsync(CancellationToken.None);

        _db.Database.SetCommandTimeout(1);

        await using (System.Data.Common.DbCommand timeoutCommand =
            _db.Database.GetDbConnection().CreateCommand())
        {
            timeoutCommand.CommandText = "PRAGMA busy_timeout=1;";

            _ = await timeoutCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        string connectionString = _db.Database.GetConnectionString()!;

        await using SqliteConnection blocker = new(connectionString);

        await blocker.OpenAsync(CancellationToken.None);

        await using (SqliteCommand begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";

            _ = await begin.ExecuteNonQueryAsync(CancellationToken.None);
        }

        PromptRepository repository = new(_db, NullLogger<PromptRepository>.Instance);

        TaskCompletionSource retryObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        repository.RetryingForTesting = (_, _, _) =>
        {
            retryObserved.SetResult();

            return ValueTask.CompletedTask;
        };

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Task<Prompt> addTask = repository.AddAsync(
            new Prompt
            {
                Id = Guid.NewGuid(),
                Name = "retry-lock",
                Version = "1",
                Template = "retry",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        try
        {
            await retryObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await using SqliteCommand commit = blocker.CreateCommand();

            commit.CommandText = "COMMIT;";

            _ = await commit.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Prompt saved = await addTask;

        Assert.NotNull(
            await repository.GetByIdAsync(saved.Id, CancellationToken.None));

    }

}
