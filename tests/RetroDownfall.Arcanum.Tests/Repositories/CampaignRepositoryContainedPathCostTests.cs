using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// Every turn that stands in a directory asks which registered root contains it, and the answer must not cost a walk of every registered
/// root's path: with thousands of registrations the lookup for a directory no root contains used to canonicalise and stat each one.
/// </summary>
[Collection("Grimoire")]
public sealed class CampaignRepositoryContainedPathCostTests : IAsyncLifetime
{
    private const int Roots = 3000;

    // A warm lookup over three thousand existing roots takes a few milliseconds. The ceiling is more than an order of magnitude above
    // that and below the cost of canonicalising each root (about a quarter of a millisecond apiece), so it fails on a regression to a
    // per-root canonical walk and not on a busy machine, because the fastest of several runs is the one compared.
    private static readonly TimeSpan Ceiling = TimeSpan.FromMilliseconds(250);

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _parent = string.Empty;

    private ArcanumDbContext? _db;

    private readonly ITestOutputHelper _output;

    public CampaignRepositoryContainedPathCostTests(GrimoireFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;

        _output = output;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _parent = Directory.CreateTempSubdirectory("arcanum-contained-cost-").FullName;

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

        _ = TestDirectoryCleanup.TryDelete(_parent, nameof(CampaignRepositoryContainedPathCostTests));
    }

    [SkippableFact]
    public async Task A_directory_no_registered_root_contains_is_found_unrelated_without_walking_every_root()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid middle = await RegisterManyAsync();

        string unrelated = Directory.CreateDirectory(Path.Combine(_parent, "elsewhere", "deep", "er")).FullName;

        ICampaignRepository repository = CreateRepository();

        Assert.Null(await repository.GetByContainedPathAsync(unrelated));

        TimeSpan fastest = TimeSpan.MaxValue;

        for (int run = 0; run < 5; run++)
        {
            Stopwatch watch = Stopwatch.StartNew();

            Campaign? found = await repository.GetByContainedPathAsync(unrelated);

            watch.Stop();

            Assert.Null(found);

            fastest = watch.Elapsed < fastest ? watch.Elapsed : fastest;
        }

        _output.WriteLine($"Fastest unrelated lookup among {Roots} roots: {fastest.TotalMilliseconds:F1} ms.");

        Assert.True(fastest < Ceiling, $"An unrelated lookup among {Roots} roots took {fastest.TotalMilliseconds:F0} ms.");

        // The cost bound does not change the answer: a directory below one of the roots is still that root's.
        string inside = Directory.CreateDirectory(Path.Combine(_parent, "roots", $"r{Roots / 2:D4}", "src")).FullName;

        Assert.Equal(middle, (await repository.GetByContainedPathAsync(inside))?.Id);
    }

    private async Task<Guid> RegisterManyAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string sanctum = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig());

        string settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault());

        Guid middle = Guid.Empty;

        for (int index = 0; index < Roots; index++)
        {
            string path = Directory.CreateDirectory(Path.Combine(_parent, "roots", $"r{index:D4}")).FullName;

            Campaign campaign = new()
            {
                Id = Guid.NewGuid(),
                Name = $"cost-{index:D4}",
                NameLower = $"cost-{index:D4}",
                Path = path,
                Type = WorkspaceType.Campaign,
                Settings = settings,
                SanctumConfigJson = sanctum,
                CreatedAt = now,
                UpdatedAt = now,
            };

            if (index == Roots / 2)
            {
                middle = campaign.Id;
            }

            _ = _db!.Campaigns.Add(campaign);
        }

        _ = await _db!.SaveChangesAsync();

        return middle;
    }

    private ICampaignRepository CreateRepository() =>
        new CampaignRepository(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            FixtureLabeledArtifactGuard.For(_db!));
}
