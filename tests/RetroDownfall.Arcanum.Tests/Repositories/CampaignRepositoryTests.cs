using System.Diagnostics;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class CampaignRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _workspaceRoot = string.Empty;

    private ArcanumDbContext? _db;

    public CampaignRepositoryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _workspaceRoot = Path.Combine(Path.GetTempPath(), "arcanum-campaign-repo", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_workspaceRoot);

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

        if (Directory.Exists(_workspaceRoot))
        {

            Directory.Delete(_workspaceRoot, recursive: true);

        }

    }

    [SkippableFact]
    public async Task AddAsync_GetByNameAsync_and_GetByPathAsync_round_trip()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string campaignDir = Path.Combine(_workspaceRoot, "alpha");

        Directory.CreateDirectory(campaignDir);

        CampaignRepository repository = CreateRepository();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = "Alpha",
            Path = campaignDir,
            Type = WorkspaceType.Campaign,
            Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Result<Campaign> addResult = await repository.AddAsync(campaign, CancellationToken.None);

        Assert.True(addResult.IsSuccess, addResult.Error.Code);

        Campaign saved = addResult.Value;

        Campaign? byId = await repository.GetByIdAsync(saved.Id, CancellationToken.None);

        Campaign? byName = await repository.GetByNameAsync("alpha", CancellationToken.None);

        Campaign? byPath = await repository.GetByPathAsync(campaignDir, CancellationToken.None);

        Assert.NotNull(byId);

        Assert.NotNull(byName);

        Assert.NotNull(byPath);

        Assert.Equal(saved.Id, byName!.Id);

        Assert.Equal(saved.Id, byPath!.Id);

    }

    [SkippableFact]
    public async Task ListAsync_UpdateAsync_and_DeleteAsync_manage_campaigns()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CampaignRepository repository = CreateRepository();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        string firstDir = Path.Combine(_workspaceRoot, "first");

        string secondDir = Path.Combine(_workspaceRoot, "second");

        Directory.CreateDirectory(firstDir);

        Directory.CreateDirectory(secondDir);

        Campaign first = (await repository.AddAsync(
            new Campaign
            {
                Id = Guid.NewGuid(),
                Name = "Zulu",
                Path = firstDir,
                Type = WorkspaceType.Campaign,
                Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None)).Value;

        Campaign second = (await repository.AddAsync(
            new Campaign
            {
                Id = Guid.NewGuid(),
                Name = "Alpha",
                Path = secondDir,
                Type = WorkspaceType.Campaign,
                Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None)).Value;

        ListPageResult<Campaign> page = await repository.ListAsync(typeFilter: null, limit: 10, cancellationToken: CancellationToken.None);

        Assert.Equal(["Alpha", "Zulu"], page.Items.Select(c => c.Name).ToArray());

        first.Name = "Zulu Prime";

        await repository.UpdateAsync(first, CancellationToken.None);

        Campaign? updated = await repository.GetByIdAsync(first.Id, CancellationToken.None);

        Assert.Equal("Zulu Prime", updated!.Name);

        Assert.Equal("zulu prime", updated.NameLower);

        bool deleted = await repository.DeleteAsync(second.Id, CancellationToken.None);

        Assert.True(deleted);

        Assert.Equal(1, await repository.CountAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task DeleteAsync_nulls_session_campaign_references()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CampaignRepository repository = CreateRepository();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        string campaignDir = Path.Combine(_workspaceRoot, "linked");

        Directory.CreateDirectory(campaignDir);

        Campaign campaign = (await repository.AddAsync(
            new Campaign
            {
                Id = Guid.NewGuid(),
                Name = "Linked",
                Path = campaignDir,
                Type = WorkspaceType.Campaign,
                Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None)).Value;

        Guid sessionId = Guid.NewGuid();

        _db!.Sessions.Add(new RetroDownfall.Arcanum.Core.Storage.Entities.Session
        {
            Id = sessionId,
            CampaignId = campaign.Id,
            CreatedAt = now,
            UpdatedAt = now,
            Status = "active",
            Title = "Linked session",
        });

        await _db.SaveChangesAsync(CancellationToken.None);

        bool deleted = await repository.DeleteAsync(campaign.Id, CancellationToken.None);

        Assert.True(deleted);

        RetroDownfall.Arcanum.Core.Storage.Entities.Session? session = await _db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None);

        Assert.NotNull(session);

        Assert.Null(session!.CampaignId);

    }

    [Fact]
    public void AddAsync_contract_returns_result()
    {

        Type returnType = typeof(ICampaignRepository)
            .GetMethod(nameof(ICampaignRepository.AddAsync))!
            .ReturnType;

        Assert.Equal(typeof(Task<Result<Campaign>>), returnType);

    }

    [SkippableFact]
    public async Task AddAsync_at_limit_returns_max_result()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CampaignRepository repository = CreateRepository(maxCampaigns: 10);

        for (int i = 0; i < 10; i++)
        {

            Result<Campaign> seeded = await repository.AddAsync(
                NewCampaign($"seed-{i}"),
                CancellationToken.None);

            Assert.True(seeded.IsSuccess, seeded.Error.Code);

        }

        Result<Campaign> result = await repository.AddAsync(
            NewCampaign("over-limit"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Campaign.MaxReached, result.Error.Code);

        Assert.Equal(10, await repository.CountAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task AddAsync_two_contexts_at_max_minus_one_only_one_succeeds()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const int maxCampaigns = 10;

        CampaignRepository seedRepository = CreateRepository(maxCampaigns: maxCampaigns);

        for (int i = 0; i < maxCampaigns - 1; i++)
        {

            Result<Campaign> seeded = await seedRepository.AddAsync(
                NewCampaign($"concurrent-seed-{i}"),
                CancellationToken.None);

            Assert.True(seeded.IsSuccess, seeded.Error.Code);

        }

        await using ArcanumDbContext firstContext = _fixture.CreateContext(_dbPath);

        await using ArcanumDbContext secondContext = _fixture.CreateContext(_dbPath);

        CampaignRepository firstRepository = CreateRepository(firstContext, maxCampaigns);

        CampaignRepository secondRepository = CreateRepository(secondContext, maxCampaigns);

        TaskCompletionSource firstTransactionBegan = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseFirstTransaction = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource secondObservedContention = new(TaskCreationOptions.RunContinuationsAsynchronously);

        firstRepository.AfterImmediateTransactionBeganForTesting = async cancellationToken =>
        {
            firstTransactionBegan.SetResult();

            await releaseFirstTransaction.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };

        secondRepository.RetryingForTesting = (_, _, _) =>
        {
            secondObservedContention.SetResult();

            return ValueTask.CompletedTask;
        };

        Task<Result<Campaign>> firstTask = firstRepository.AddAsync(
            NewCampaign("concurrent-first"),
            CancellationToken.None);

        await firstTransactionBegan.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<Result<Campaign>> secondTask = secondRepository.AddAsync(
            NewCampaign("concurrent-second"),
            CancellationToken.None);

        await secondObservedContention.Task.WaitAsync(TimeSpan.FromSeconds(10));

        releaseFirstTransaction.SetResult();

        Result<Campaign>[] results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => result.IsSuccess);

        Result<Campaign> rejected = Assert.Single(results, result => result.IsFailure);

        Assert.Equal(ErrorCodes.Campaign.MaxReached, rejected.Error.Code);

        await using ArcanumDbContext verificationContext = _fixture.CreateContext(_dbPath);

        Assert.Equal(
            maxCampaigns,
            await verificationContext.Campaigns.CountAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task AddAsync_unrelated_write_failure_is_not_mapped_to_max()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CampaignRepository repository = CreateRepository(maxCampaigns: 10);

        Result<Campaign> first = await repository.AddAsync(
            NewCampaign("duplicate-name"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);

        Campaign duplicate = NewCampaign("duplicate-name");

        _ = await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(duplicate, CancellationToken.None));

        Assert.Equal(EntityState.Detached, _db!.Entry(duplicate).State);

        await using ArcanumDbContext verificationContext = _fixture.CreateContext(_dbPath);

        Assert.Equal(1, await verificationContext.Campaigns.CountAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task AddAsync_exhausted_lock_failure_does_not_report_max_or_poison_entity_state()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _db!.Database.OpenConnectionAsync(CancellationToken.None);

        SqliteConnection repositoryConnection =
            Assert.IsType<SqliteConnection>(_db.Database.GetDbConnection());

        repositoryConnection.DefaultTimeout = 1;

        await using SqliteConnection blocker =
            new(_db.Database.GetConnectionString());

        await blocker.OpenAsync(CancellationToken.None);

        await using (SqliteCommand begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";

            _ = await begin.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Campaign campaign = NewCampaign("locked-campaign");

        CampaignRepository repository = CreateRepository(maxCampaigns: 10);

        SqliteException failure;

        try
        {
            failure = await Assert.ThrowsAsync<SqliteException>(
                () => repository.AddAsync(campaign, CancellationToken.None));
        }
        finally
        {
            await using SqliteCommand rollback = blocker.CreateCommand();

            rollback.CommandText = "ROLLBACK;";

            _ = await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Assert.Contains(failure.SqliteErrorCode, new[] { 5, 6 });

        Assert.Equal(EntityState.Detached, _db.Entry(campaign).State);

        repositoryConnection.DefaultTimeout = 30;

        Result<Campaign> retry = await repository.AddAsync(
            campaign,
            CancellationToken.None);

        Assert.True(retry.IsSuccess, retry.Error.Code);

        Assert.Equal(1, await repository.CountAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task AddAsync_canceled_during_contention_is_bounded()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await _db!.Database.OpenConnectionAsync(CancellationToken.None);

        await using SqliteConnection blocker =
            new(_db.Database.GetConnectionString());

        await blocker.OpenAsync(CancellationToken.None);

        await using (SqliteCommand begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";

            _ = await begin.ExecuteNonQueryAsync(CancellationToken.None);
        }

        CampaignRepository repository = CreateRepository(maxCampaigns: 10);

        using CancellationTokenSource cancellation = new();

        TaskCompletionSource firstBoundedAttemptFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        repository.RetryingForTesting = (_, _, _) =>
        {
            firstBoundedAttemptFinished.SetResult();

            cancellation.Cancel();

            return ValueTask.CompletedTask;
        };

        Stopwatch elapsed = Stopwatch.StartNew();

        try
        {
            Task<Result<Campaign>> addTask = repository.AddAsync(
                NewCampaign("cancelled-contention"),
                cancellation.Token);

            await firstBoundedAttemptFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => addTask);
        }
        finally
        {
            await using SqliteCommand rollback = blocker.CreateCommand();

            rollback.CommandText = "ROLLBACK;";

            _ = await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }

        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(3),
            $"Cancellation took {elapsed.Elapsed}; expected one bounded acquisition attempt plus retry cancellation.");

    }

    private Campaign NewCampaign(string suffix)
    {

        string campaignDir = Path.Combine(_workspaceRoot, suffix);

        Directory.CreateDirectory(campaignDir);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new Campaign
        {
            Id = Guid.NewGuid(),
            Name = suffix,
            Path = campaignDir,
            Type = WorkspaceType.Campaign,
            Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
            CreatedAt = now,
            UpdatedAt = now,
        };

    }

    private CampaignRepository CreateRepository(
        ArcanumDbContext? db = null,
        int maxCampaigns = 500)
    {

        ArcanumSettings settings = new()
        {
            Campaigns = new CampaignsSettings { MaxCampaigns = maxCampaigns },
        };

        return new CampaignRepository(
            db ?? _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(settings));

    }

}
