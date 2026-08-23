using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SessionEntity = RetroDownfall.Arcanum.Core.Storage.Entities.Session;

namespace RetroDownfall.Arcanum.Tests.Api.Health;

/// <summary>
/// Resolves the service from a dedicated test host so both EF counts and physical database paths
/// point at the same isolated Grimoire.
/// </summary>
[Collection("ApiHost")]
public sealed class GrimoireStatsServiceTests
{

    [SkippableFact]
    public async Task GetStatsAsync_ReportsPhysicalSizesAndEntityCounts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        using IServiceScope scope = factory.Services.CreateScope();
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = "Stats campaign",
            NameLower = "stats campaign",
            Path = Path.Combine(factory.TempHome, "stats-campaign"),
            Type = WorkspaceType.Campaign,
            Settings = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        SessionEntity session = new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            Title = "Stats session",
            CreatedAt = now,
            UpdatedAt = now,
        };
        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Role = MessageRole.User,
            Content = "Count me",
            ModelUsed = "test-model",
            CreatedAt = now,
            Sequence = 1,
        };
        db.Campaigns.Add(campaign);
        db.Sessions.Add(session);
        db.Entries.Add(entry);
        await db.SaveChangesAsync();

        string databasePath = ArcanumPaths.GrimoireDatabaseFile;
        string walPath = databasePath + "-wal";
        Assert.StartsWith(factory.TempHome, databasePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(databasePath));
        Assert.True(File.Exists(walPath));

        GrimoireStatsService service = scope.ServiceProvider.GetRequiredService<GrimoireStatsService>();
        GrimoireStatsDto stats = await service.GetStatsAsync(CancellationToken.None);

        Assert.Equal(new FileInfo(databasePath).Length, stats.DatabaseBytes);
        Assert.Equal(new FileInfo(walPath).Length, stats.WalBytes);
        Assert.True(stats.DatabaseBytes > 0);
        Assert.True(stats.WalBytes > 0);
        Assert.Equal(1, stats.SessionCount);
        Assert.Equal(1, stats.EntryCount);
        Assert.Equal(1, stats.CampaignCount);

    }

}
