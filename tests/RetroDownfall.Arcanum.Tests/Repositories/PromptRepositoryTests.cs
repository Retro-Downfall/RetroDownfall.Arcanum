using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

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

        Prompt saved = await repository.AddSucceededAsync(prompt, CancellationToken.None);

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

        Prompt alpha = await repository.AddSucceededAsync(
            CreatePrompt(null, "alpha", "1", now.AddMinutes(-3)),
            CancellationToken.None);

        Prompt summonV1 = await repository.AddSucceededAsync(
            CreatePrompt(null, "summon", "1", now.AddMinutes(-2)),
            CancellationToken.None);

        Prompt summonV2 = await repository.AddSucceededAsync(
            CreatePrompt(null, "summon", "2", now.AddMinutes(-1)),
            CancellationToken.None);

        Prompt campaignSummon = await repository.AddSucceededAsync(
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

    /// <summary>
    /// A prompt list page must be cut in SQL with the same (Name, UpdatedAt) order the client used to
    /// apply, so rows outside the page are never loaded.
    /// </summary>
    [SkippableFact]
    public async Task ListAsync_reads_only_the_requested_page_from_storage()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Dictionary<string, Prompt> byName = [];

        foreach (string name in new[] { "echo", "alpha", "delta", "bravo", "charlie", "foxtrot" })
        {
            byName[name] = await repository.AddSucceededAsync(
                CreatePrompt(null, name, "1", now.AddMinutes(-byName.Count)),
                CancellationToken.None);
        }

        // Two versions of one name sort newest first within the name.
        Prompt alphaNewer = await repository.AddSucceededAsync(
            CreatePrompt(null, "alpha", "2", now),
            CancellationToken.None);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        List<string> statements = [];

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => statements.Add(sql), null);

        ListPageResult<Prompt> page;

        try
        {
            page = await repository.ListAsync(
                campaignId: null,
                limit: 3,
                offset: 2,
                CancellationToken.None);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        // alpha v2, alpha v1, bravo, charlie, delta, echo, foxtrot; offset 2 and three rows.
        Assert.Equal(
            [byName["bravo"].Id, byName["charlie"].Id, byName["delta"].Id],
            page.Items.Select(static prompt => prompt.Id));

        Assert.True(page.HasMore);

        Assert.Equal(5, page.NextOffset);

        Assert.Equal(alphaNewer.Id, (await repository.ListAsync(
            campaignId: null,
            limit: 1,
            offset: 0,
            CancellationToken.None)).Items.Single().Id);

        string[] selects =
        [
            .. statements.Where(static sql =>
                sql.Contains("FROM \"Prompts\"", StringComparison.Ordinal)),
        ];

        Assert.NotEmpty(selects);

        Assert.All(selects, static sql => Assert.Matches(@"\bLIMIT\b", sql));
    }

    [SkippableFact]
    public async Task DeleteAsync_joins_the_context_transaction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        Prompt prompt = await repository.AddSucceededAsync(
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

        Prompt alpha = await repository.AddSucceededAsync(
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

        Prompt beta = await repository.AddSucceededAsync(
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

        _ = await repository.UpdateSucceededAsync(alpha, CancellationToken.None);

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

        Task<Prompt> addTask = repository.AddSucceededAsync(
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

    [SkippableFact]
    public async Task AddAsync_maps_a_unique_index_violation_to_DuplicateVersion_and_leaves_the_context_usable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        _ = await repository.AddSucceededAsync(CreatePrompt(null, "taken", "1", now), CancellationToken.None);

        Result<Prompt> loser = await repository.AddAsync(CreatePrompt(null, "taken", "1", now), CancellationToken.None);

        Assert.True(loser.IsFailure);

        Assert.Equal(ErrorCodes.Prompt.DuplicateVersion, loser.Error.Code);

        // The failed entity must not stay tracked, or this unrelated write would replay it.
        _ = await repository.AddSucceededAsync(CreatePrompt(null, "free", "1", now), CancellationToken.None);

        Assert.Equal(
            ["free", "taken"],
            (await repository.ListAsync(null, cancellationToken: CancellationToken.None)).Items.Select(static p => p.Name));
    }

    [SkippableFact]
    public async Task AddAsync_maps_a_missing_campaign_to_CampaignNotFound()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        Result<Prompt> result = await repository.AddAsync(
            CreatePrompt(Guid.NewGuid(), "orphan", "1", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Campaign.NotFound, result.Error.Code);
    }

    [SkippableFact]
    public async Task UpdateAsync_maps_a_unique_index_violation_to_DuplicateVersion_and_changes_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        _ = await repository.AddSucceededAsync(CreatePrompt(null, "alpha", "1", now), CancellationToken.None);

        Prompt beta = await repository.AddSucceededAsync(CreatePrompt(null, "beta", "1", now), CancellationToken.None);

        beta.Name = "alpha";

        Result<Prompt> result = await repository.UpdateAsync(beta, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Prompt.DuplicateVersion, result.Error.Code);

        Assert.Equal("beta", (await repository.GetByIdAsync(beta.Id, CancellationToken.None))!.Name);
    }

    [SkippableFact]
    public async Task ReplaceCampaignPromptsAsync_swaps_the_campaign_set_and_leaves_other_scopes_alone()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid target = await SeedCampaignAsync("replace-target");

        Guid other = await SeedCampaignAsync("replace-other");

        Prompt targetAlpha = await SeedPromptAsync(target, "alpha", "1", now);

        Prompt targetBeta = await SeedPromptAsync(target, "beta", "1", now);

        Prompt otherAlpha = await SeedPromptAsync(other, "alpha", "1", now);

        Prompt globalAlpha = await SeedPromptAsync(null, "alpha", "1", now);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        // "alpha" 1 reuses the name and version of a prompt being deleted, which the unique index only
        // admits when the delete and the add share a transaction.
        Prompt incomingAlpha = CreatePrompt(null, "alpha", "1", now);

        incomingAlpha.Template = "incoming";

        Result<int> result = await repository.ReplaceCampaignPromptsAsync(
            target,
            [incomingAlpha, CreatePrompt(null, "gamma", "1", now)],
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, result.Value);

        ListPageResult<Prompt> after = await repository.ListAsync(target, cancellationToken: CancellationToken.None);

        Assert.Equal(["alpha", "gamma"], after.Items.Select(static p => p.Name));

        Assert.All(after.Items, p => Assert.Equal(target, p.CampaignId));

        Assert.Equal("incoming", after.Items[0].Template);

        Assert.DoesNotContain(after.Items, p => p.Id == targetAlpha.Id || p.Id == targetBeta.Id);

        Assert.NotNull(await repository.GetByIdAsync(otherAlpha.Id, CancellationToken.None));

        Assert.NotNull(await repository.GetByIdAsync(globalAlpha.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task ReplaceCampaignPromptsAsync_rolls_back_the_delete_when_an_add_fails()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid target = await SeedCampaignAsync("replace-rollback");

        Prompt kept = await SeedPromptAsync(target, "kept", "1", now);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        // The second add collides with the first on (name, version, campaign).
        Result<int> result = await repository.ReplaceCampaignPromptsAsync(
            target,
            [CreatePrompt(null, "twin", "1", now), CreatePrompt(null, "twin", "1", now)],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Prompt.DuplicateVersion, result.Error.Code);

        ListPageResult<Prompt> after = await repository.ListAsync(target, cancellationToken: CancellationToken.None);

        Assert.Equal(kept.Id, Assert.Single(after.Items).Id);

        // The context is still usable: a clean replacement afterwards succeeds.
        Result<int> retried = await repository.ReplaceCampaignPromptsAsync(
            target,
            [CreatePrompt(null, "twin", "1", now)],
            CancellationToken.None);

        Assert.True(retried.IsSuccess);

        Assert.Equal("twin", Assert.Single((await repository.ListAsync(target, cancellationToken: CancellationToken.None)).Items).Name);
    }

    [SkippableFact]
    public async Task ReplaceCampaignPromptsAsync_into_an_unknown_campaign_fails_without_writing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        Guid missing = Guid.NewGuid();

        Result<int> result = await repository.ReplaceCampaignPromptsAsync(
            missing,
            [CreatePrompt(null, "orphan", "1", DateTimeOffset.UtcNow)],
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Campaign.NotFound, result.Error.Code);

        Assert.Empty((await repository.ListAsync(missing, cancellationToken: CancellationToken.None)).Items);
    }

    [SkippableFact]
    public async Task ReplaceCampaignPromptsAsync_with_a_cancelled_token_changes_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid target = await SeedCampaignAsync("replace-cancelled");

        Prompt kept = await SeedPromptAsync(target, "kept", "1", now);

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance);

        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ReplaceCampaignPromptsAsync(
                target,
                [CreatePrompt(null, "new", "1", now)],
                cancellation.Token));

        Assert.Equal(
            kept.Id,
            Assert.Single((await repository.ListAsync(target, cancellationToken: CancellationToken.None)).Items).Id);
    }

    [SkippableFact]
    public async Task ReplaceCampaignPromptsAsync_finishes_once_the_delete_has_run_even_if_the_token_is_cancelled()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid target = await SeedCampaignAsync("replace-point-of-no-return");

        _ = await SeedPromptAsync(target, "old", "1", now);

        using CancellationTokenSource cancellation = new();

        PromptRepository repository = new(_db!, NullLogger<PromptRepository>.Instance)
        {
            // The caller disconnects the instant the first delete has run: the swap is past the point where
            // stopping is cheaper than finishing, so the adds and the commit must not honour the token.
            AfterReplaceDeleteForTesting = async _ => await cancellation.CancelAsync(),
        };

        Result<int> result = await repository.ReplaceCampaignPromptsAsync(
            target,
            [CreatePrompt(null, "new", "1", now), CreatePrompt(null, "newer", "1", now)],
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);

        Assert.True(result.IsSuccess);

        Assert.Equal(
            ["new", "newer"],
            (await repository.ListAsync(target, cancellationToken: CancellationToken.None)).Items.Select(static p => p.Name));
    }

    private async Task<Guid> SeedCampaignAsync(string name)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid id = Guid.NewGuid();

        _db!.Campaigns.Add(new Campaign
        {
            Id = id,
            Name = name,
            NameLower = name,
            Path = Path.Combine(Path.GetTempPath(), $"prompt-campaign-{id:N}"),
            Type = WorkspaceType.Campaign,
            Settings = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

        return id;
    }

    private async Task<Prompt> SeedPromptAsync(Guid? campaignId, string name, string version, DateTimeOffset timestamp)
    {
        Prompt prompt = CreatePrompt(campaignId, name, version, timestamp);

        _db!.Prompts.Add(prompt);

        _ = await _db.SaveChangesAsync(CancellationToken.None);

        return prompt;
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

internal static class PromptRepositoryTestExtensions
{
    public static async Task<Prompt> AddSucceededAsync(
        this PromptRepository repository,
        Prompt prompt,
        CancellationToken cancellationToken)
    {
        Result<Prompt> result = await repository.AddAsync(prompt, cancellationToken);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;
    }

    public static async Task<Prompt> UpdateSucceededAsync(
        this PromptRepository repository,
        Prompt prompt,
        CancellationToken cancellationToken)
    {
        Result<Prompt> result = await repository.UpdateAsync(prompt, cancellationToken);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;
    }
}
