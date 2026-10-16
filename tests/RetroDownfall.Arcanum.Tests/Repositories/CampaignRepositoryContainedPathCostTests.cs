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
/// root's path: with thousands of registrations the lookup for a directory no root contains used to canonicalise and stat each one. The
/// two attachment source routes ask the same question of the path as it is spelled, and the same bound holds for them.
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

    [SkippableFact]
    public async Task A_spelled_path_no_registered_root_contains_is_found_unrelated_without_walking_every_root()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid middle = await RegisterManyAsync();

        string unrelated = Path.Combine(Directory.CreateDirectory(Path.Combine(_parent, "elsewhere", "deep", "er")).FullName, "notes.txt");

        ICampaignRepository repository = CreateRepository();

        Assert.Null(await repository.GetBySpelledPathAsync(unrelated));

        TimeSpan fastest = TimeSpan.MaxValue;

        for (int run = 0; run < 5; run++)
        {
            Stopwatch watch = Stopwatch.StartNew();

            Campaign? found = await repository.GetBySpelledPathAsync(unrelated);

            watch.Stop();

            Assert.Null(found);

            fastest = watch.Elapsed < fastest ? watch.Elapsed : fastest;
        }

        _output.WriteLine($"Fastest unrelated spelled lookup among {Roots} roots: {fastest.TotalMilliseconds:F1} ms.");

        Assert.True(fastest < Ceiling, $"An unrelated spelled lookup among {Roots} roots took {fastest.TotalMilliseconds:F0} ms.");

        // The cost bound does not change the answer: a path spelled below one of the roots is still that root's.
        string middleRoot = Path.Combine(_parent, "roots", $"r{Roots / 2:D4}");

        string inside = Path.Combine(Directory.CreateDirectory(Path.Combine(middleRoot, "src")).FullName, "notes.txt");

        Assert.Equal(middle, (await repository.GetBySpelledPathAsync(inside))?.Id);
    }

    // The root a lookup need not walk is the one whose directory the path's own spelling does not name. A root that is named, because the
    // path stands inside it or because the root is spelled through a link to the directory the path names, is still walked and found, with
    // thousands of unrelated roots registered beside it.
    [SkippableFact]
    public async Task A_spelled_path_is_still_found_through_a_link_among_thousands_of_roots()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        Guid middle = await RegisterManyAsync();

        ICampaignRepository repository = CreateRepository();

        string elsewhere = Directory.CreateDirectory(Path.Combine(_parent, "elsewhere", "deep", "er")).FullName;

        string middleRoot = Path.Combine(_parent, "roots", $"r{Roots / 2:D4}");

        // A path spelled through a link inside one root is spelled inside it, whatever the link points at...
        Directory.CreateSymbolicLink(Path.Combine(middleRoot, "out"), Path.Combine(_parent, "elsewhere"));

        Assert.Equal(middle, (await repository.GetBySpelledPathAsync(Path.Combine(middleRoot, "out", "deep", "er", "notes.txt")))?.Id);

        // ...and the directory the link points at is no part of the root when it is spelled on its own.
        Assert.Null(await repository.GetBySpelledPathAsync(Path.Combine(elsewhere, "notes.txt")));

        // A root registered through a link is found from the physical spelling of the directory it reaches.
        string real = Directory.CreateDirectory(Path.Combine(PhysicalTestPaths.Resolve(_parent), "real-linked-root", "src")).Parent!.FullName;

        string alias = Path.Combine(_parent, "alias");

        Directory.CreateSymbolicLink(alias, real);

        Guid linked = await RegisterOneAsync("cost-linked", alias);

        Assert.Equal(linked, (await repository.GetBySpelledPathAsync(Path.Combine(real, "src", "notes.txt")))?.Id);

        // A name-prefix sibling of the directory the root reaches is not inside it.
        Assert.Null(await repository.GetBySpelledPathAsync(Path.Combine(PhysicalTestPaths.Resolve(_parent), "real-linked-root-legacy", "notes.txt")));
    }

    private async Task<Guid> RegisterOneAsync(string name, string path)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            NameLower = name,
            Path = path,
            Type = WorkspaceType.Campaign,
            Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ = _db!.Campaigns.Add(campaign);

        _ = await _db!.SaveChangesAsync();

        return campaign.Id;
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
