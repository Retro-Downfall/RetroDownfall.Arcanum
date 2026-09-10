using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
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
            Description = "A fully populated prompt",
            Template = "Hello {{name}}",
            Tags = PromptRepository.SerializeTags(["forge", "greeting"]),
            ParameterSchema = "{\"type\":\"object\"}",
            DefaultParameters = "{\"name\":\"wizard\"}",
            Model = "model",
            Provider = "provider",
            Temperature = 0.25,
            TopP = 0.75,
            MaxOutputTokens = 512,
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

        Assert.Equal(prompt.Description, byId.Description);

        Assert.Equal(prompt.ParameterSchema, byId.ParameterSchema);

        Assert.Equal(prompt.DefaultParameters, byId.DefaultParameters);

        Assert.Equal(prompt.Model, byId.Model);

        Assert.Equal(prompt.Provider, byId.Provider);

        Assert.Equal(prompt.Temperature, byId.Temperature);

        Assert.Equal(prompt.TopP, byId.TopP);

        Assert.Equal(prompt.MaxOutputTokens, byId.MaxOutputTokens);
    }

    [SkippableFact]
    public async Task ListVersionsAsync_and_ListAsync_preserve_scope_order_and_paging()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid campaignId = Guid.NewGuid();

        _db!.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            Name = "Prompt campaign",
            NameLower = "prompt campaign",
            Path = Path.Combine(Path.GetTempPath(), $"prompt-campaign-{campaignId:N}"),
            Type = WorkspaceType.Campaign,
            Settings = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

        PromptRepository repository = new(_db, NullLogger<PromptRepository>.Instance);

        Prompt alpha = await repository.AddAsync(
            CreatePrompt(null, "alpha", "1", now.AddMinutes(-3)),
            CancellationToken.None);

        Prompt summonV1 = await repository.AddAsync(
            CreatePrompt(null, "summon", "1", now.AddMinutes(-2)),
            CancellationToken.None);

        Prompt summonV2 = await repository.AddAsync(
            CreatePrompt(null, "summon", "2", now.AddMinutes(-1)),
            CancellationToken.None);

        Prompt campaignSummon = await repository.AddAsync(
            CreatePrompt(campaignId, "summon", "campaign", now),
            CancellationToken.None);

        IReadOnlyList<Prompt> globalVersions = await repository.ListVersionsAsync(
            " summon ",
            campaignId: null,
            CancellationToken.None);

        IReadOnlyList<Prompt> campaignVersions = await repository.ListVersionsAsync(
            "summon",
            campaignId,
            CancellationToken.None);

        ListPageResult<Prompt> firstPage = await repository.ListAsync(
            campaignId: null,
            limit: 2,
            offset: 0,
            CancellationToken.None);

        ListPageResult<Prompt> secondPage = await repository.ListAsync(
            campaignId: null,
            limit: 2,
            offset: firstPage.NextOffset!.Value,
            CancellationToken.None);

        ListPageResult<Prompt> campaignPage = await repository.ListAsync(
            campaignId,
            limit: 10,
            cancellationToken: CancellationToken.None);

        Assert.Equal([summonV2.Id, summonV1.Id], globalVersions.Select(static prompt => prompt.Id));

        Assert.Equal([campaignSummon.Id], campaignVersions.Select(static prompt => prompt.Id));

        Assert.Equal([alpha.Id, summonV2.Id], firstPage.Items.Select(static prompt => prompt.Id));

        Assert.True(firstPage.HasMore);

        Assert.Equal(2, firstPage.NextOffset);

        Assert.Equal([summonV1.Id], secondPage.Items.Select(static prompt => prompt.Id));

        Assert.False(secondPage.HasMore);

        Assert.Equal([campaignSummon.Id], campaignPage.Items.Select(static prompt => prompt.Id));
    }

    [SkippableFact]
    public async Task DeleteAsync_joins_the_context_transaction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        Prompt prompt = await repository.AddAsync(
            CreatePrompt(null, "rollback", "1", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await _db!.Database.BeginTransactionAsync(CancellationToken.None);

        Assert.True(await repository.DeleteAsync(prompt.Id, CancellationToken.None));

        Assert.Null(await repository.GetByIdAsync(prompt.Id, CancellationToken.None));

        await transaction.RollbackAsync(CancellationToken.None);

        await transaction.DisposeAsync();

        Assert.NotNull(await repository.GetByIdAsync(prompt.Id, CancellationToken.None));
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

    private static Prompt CreatePrompt(
        Guid? campaignId,
        string name,
        string version,
        DateTimeOffset timestamp) =>
        new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Name = name,
            Version = version,
            Template = $"{name}:{version}",
            Tags = "[]",
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
}
