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

        Campaign saved = await repository.AddAsync(campaign, CancellationToken.None);

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

        Campaign first = await repository.AddAsync(
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
            CancellationToken.None);

        Campaign second = await repository.AddAsync(
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
            CancellationToken.None);

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

        Campaign campaign = await repository.AddAsync(
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
            CancellationToken.None);

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

    private CampaignRepository CreateRepository()
    {
        return new CampaignRepository(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

    }

}
