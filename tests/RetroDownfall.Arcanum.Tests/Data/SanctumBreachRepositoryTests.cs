using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SanctumBreachRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SanctumBreachRepository? _repository;

    public SanctumBreachRepositoryTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _repository = new SanctumBreachRepository(_db);

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
    public async Task RecordAsync_then_QueryAsync_round_trips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string campaignId = await SeedCampaignAsync();

        SanctumBreachDetails details = new(
            RequestedPath: "/tmp/secret/../escape.txt",
            ResolvedPath: "/etc/escape.txt",
            WorkspaceRoot: "/tmp/secret",
            RequestedUrl: null,
            ToolArguments: """{"path":"../escape.txt"}""",
            LimitValue: null,
            ActualValue: null);

        SanctumBreachRecord record = new(
            Id: "ignored-by-repository",
            CampaignId: campaignId,
            OccurredAt: DateTimeOffset.Parse("2026-07-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ToolName: "read_file_chunk",
            BreachType: "PathEscape",
            Description: "Path would leave the campaign workspace.",
            Details: details);

        await _repository!.RecordAsync(record, maxBreachCount: 1000, CancellationToken.None);

        IReadOnlyList<SanctumBreachRecord> results = await _repository.QueryAsync(campaignId, limit: 10, ct: CancellationToken.None);

        Assert.Single(results);

        SanctumBreachRecord loaded = results[0];

        Assert.NotEmpty(loaded.Id);

        // CampaignId is normalized to match EF's uppercase Guid text representation on write.
        Assert.Equal(campaignId, loaded.CampaignId, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("read_file_chunk", loaded.ToolName);

        Assert.Equal("PathEscape", loaded.BreachType);

        Assert.Equal("Path would leave the campaign workspace.", loaded.Description);

        Assert.NotNull(loaded.Details);

        Assert.Equal("/tmp/secret/../escape.txt", loaded.Details!.RequestedPath);

        Assert.Equal("/etc/escape.txt", loaded.Details.ResolvedPath);

        Assert.Equal("/tmp/secret", loaded.Details.WorkspaceRoot);

        Assert.Equal("""{"path":"../escape.txt"}""", loaded.Details.ToolArguments);

    }

    [SkippableFact]
    public async Task QueryAsync_filters_by_tool_name()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string campaignId = await SeedCampaignAsync();

        await RecordSimpleBreachAsync(campaignId, "network_fetch", "NetworkEgress");

        await RecordSimpleBreachAsync(campaignId, "read_file_chunk", "PathEscape");

        IReadOnlyList<SanctumBreachRecord> filtered = await _repository!.QueryAsync(
            campaignId,
            limit: 10,
            toolName: "read_file_chunk",
            ct: CancellationToken.None);

        Assert.Single(filtered);

        Assert.Equal("read_file_chunk", filtered[0].ToolName);

    }

    [SkippableFact]
    public async Task QueryAsync_filters_by_before_cursor()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string campaignId = await SeedCampaignAsync();

        DateTimeOffset older = DateTimeOffset.Parse("2026-07-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        DateTimeOffset newer = DateTimeOffset.Parse("2026-07-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        await RecordSimpleBreachAsync(campaignId, "tool-old", "PathEscape", older);

        await RecordSimpleBreachAsync(campaignId, "tool-new", "PathEscape", newer);

        IReadOnlyList<SanctumBreachRecord> results = await _repository!.QueryAsync(
            campaignId,
            limit: 10,
            before: newer,
            ct: CancellationToken.None);

        Assert.Single(results);

        Assert.Equal("tool-old", results[0].ToolName);

    }

    [SkippableFact]
    public async Task QueryAsync_respects_limit()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string campaignId = await SeedCampaignAsync();

        for (int i = 0; i < 5; i++)
        {

            await RecordSimpleBreachAsync(campaignId, $"tool-{i}", "PathEscape");

        }

        IReadOnlyList<SanctumBreachRecord> results = await _repository!.QueryAsync(campaignId, limit: 2, ct: CancellationToken.None);

        Assert.Equal(2, results.Count);

    }

    [SkippableFact]
    public async Task DeleteOldestAsync_removes_correct_rows()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string campaignId = await SeedCampaignAsync();

        for (int i = 0; i < 5; i++)
        {

            await RecordSimpleBreachAsync(
                campaignId,
                $"tool-{i}",
                "PathEscape",
                DateTimeOffset.UtcNow.AddMinutes(i));

        }

        int deleted = await _repository!.DeleteOldestAsync(campaignId, count: 2, CancellationToken.None);

        Assert.Equal(2, deleted);

        IReadOnlyList<SanctumBreachRecord> remaining = await _repository.QueryAsync(campaignId, limit: 10, ct: CancellationToken.None);

        Assert.Equal(3, remaining.Count);

        Assert.DoesNotContain(remaining, r => r.ToolName is "tool-0" or "tool-1");

    }

    [SkippableFact]
    public async Task Retention_enforced_on_insert()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string campaignId = await SeedCampaignAsync();

        const int maxBreachCount = 100;

        for (int i = 0; i < maxBreachCount + 3; i++)
        {

            SanctumBreachRecord record = new(
                Id: "ignored",
                CampaignId: campaignId,
                OccurredAt: DateTimeOffset.UtcNow.AddSeconds(i),
                ToolName: $"tool-{i}",
                BreachType: "PathEscape",
                Description: $"breach {i}",
                Details: null);

            await _repository!.RecordAsync(record, maxBreachCount, CancellationToken.None);

        }

        int count = await _repository!.GetCountAsync(campaignId, CancellationToken.None);

        Assert.Equal(maxBreachCount, count);

        IReadOnlyList<SanctumBreachRecord> remaining = await _repository.QueryAsync(campaignId, limit: maxBreachCount, ct: CancellationToken.None);

        Assert.DoesNotContain(remaining, r => r.ToolName is "tool-0" or "tool-1" or "tool-2");

        Assert.Contains(remaining, r => r.ToolName == $"tool-{maxBreachCount + 2}");

    }

    [SkippableFact]
    public async Task Migration_creates_SanctumBreaches_table()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        await using System.Data.Common.DbCommand cmd = connection.CreateCommand();

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {

            await cmd.Connection.OpenAsync(CancellationToken.None);

        }

        cmd.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'SanctumBreaches'
            LIMIT 1;
            """;

        object? result = await cmd.ExecuteScalarAsync(CancellationToken.None);

        Assert.NotNull(result);

    }

    private async Task RecordSimpleBreachAsync(
        string campaignId,
        string toolName,
        string breachType,
        DateTimeOffset? occurredAt = null)
    {

        SanctumBreachRecord record = new(
            Id: "ignored",
            CampaignId: campaignId,
            OccurredAt: occurredAt ?? DateTimeOffset.UtcNow,
            ToolName: toolName,
            BreachType: breachType,
            Description: $"{breachType} via {toolName}",
            Details: null);

        await _repository!.RecordAsync(record, maxBreachCount: 1000, CancellationToken.None);

    }

    private async Task<string> SeedCampaignAsync()
    {
        CampaignRepository campaignRepository = new(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

        string workspaceRoot = Path.Combine(Path.GetTempPath(), "arcanum-sanctum-breach-repo", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workspaceRoot);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Campaign-{Guid.NewGuid():N}",
            Path = workspaceRoot,
            Type = WorkspaceType.Campaign,
            Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Campaign saved = (await campaignRepository
            .AddAsync(campaign, CancellationToken.None)).Value;

        return saved.Id.ToString();

    }

}
