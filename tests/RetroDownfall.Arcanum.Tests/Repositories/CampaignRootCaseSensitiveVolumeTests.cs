using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// Case is folded per platform in the text comparisons, but whether two spellings are one directory is decided per volume. On a
/// case-sensitive volume <c>proj</c> and <c>Proj</c> are two directories, so a registered root governs the one it names and no other, a
/// case-sibling registration does not outrank the real parent, and the two can be registered side by side. Every test here needs a real
/// case-sensitive volume and skips where one cannot be attached.
/// </summary>
[Collection("Grimoire")]
public sealed class CampaignRootCaseSensitiveVolumeTests : IClassFixture<CaseSensitiveVolumeFixture>, IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private readonly CaseSensitiveVolumeFixture _volume;

    private string _dbPath = string.Empty;

    private string _parent = string.Empty;

    private ArcanumDbContext? _db;

    public CampaignRootCaseSensitiveVolumeTests(GrimoireFixture fixture, CaseSensitiveVolumeFixture volume)
    {
        _fixture = fixture;

        _volume = volume;
    }

    public Task InitializeAsync()
    {
        if (!GrimoireFixture.SqlCipherAvailable || _volume.Volume is null)
        {
            return Task.CompletedTask;
        }

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _parent = Directory.CreateDirectory(Path.Combine(_volume.Volume.Root, Guid.NewGuid().ToString("N"))).FullName;

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

        if (_parent.Length > 0)
        {
            _ = TestDirectoryCleanup.TryDelete(_parent, nameof(CampaignRootCaseSensitiveVolumeTests));
        }
    }

    [SkippableFact]
    public async Task A_case_sibling_of_a_registered_root_is_a_different_directory_and_is_not_contained()
    {
        RequireVolume();

        string root = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        string sibling = Directory.CreateDirectory(Path.Combine(_parent, "Proj", "src")).FullName;

        _ = Directory.CreateDirectory(Path.Combine(root, "src"));

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "proj", root, enabled: true);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(Path.Combine(root, "src")))?.Id);

        Assert.Null(await repository.GetByContainedPathAsync(sibling));

        Assert.Null(await repository.GetByContainedPathAsync(Path.Combine(_parent, "Proj")));
    }

    [SkippableFact]
    public async Task A_case_sibling_registration_does_not_outrank_the_real_parent()
    {
        RequireVolume();

        string outer = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        string realChild = Directory.CreateDirectory(Path.Combine(outer, "src", "deep")).FullName;

        string caseSiblingOfTheChild = Directory.CreateDirectory(Path.Combine(outer, "Src")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign strict = await AddAsync(repository, "outer-strict", outer, enabled: true);

        Campaign open = await AddAsync(repository, "sibling-open", caseSiblingOfTheChild, enabled: false);

        // The directory inside outer/src is not inside outer/Src, so it is governed by the Campaign that really contains it.
        Assert.Equal(strict.Id, (await repository.GetByContainedPathAsync(realChild))?.Id);

        // The sibling's own directory is governed by its own registration.
        Assert.Equal(open.Id, (await repository.GetByContainedPathAsync(caseSiblingOfTheChild))?.Id);
    }

    [SkippableFact]
    public async Task Two_directories_that_differ_only_by_case_can_be_registered_and_are_found_by_their_own_path()
    {
        RequireVolume();

        string lower = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        string upper = Directory.CreateDirectory(Path.Combine(_parent, "Proj")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign first = await AddAsync(repository, "proj-lower", lower, enabled: true);

        Campaign second = await AddAsync(repository, "proj-upper", upper, enabled: false);

        Assert.Equal(first.Id, (await repository.GetByPathAsync(lower))?.Id);

        Assert.Equal(second.Id, (await repository.GetByPathAsync(upper))?.Id);
    }

    [SkippableFact]
    public async Task The_path_boundary_of_a_root_never_admits_its_case_sibling()
    {
        RequireVolume();

        string root = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        string sibling = Directory.CreateDirectory(Path.Combine(_parent, "Proj")).FullName;

        await File.WriteAllTextAsync(Path.Combine(root, "inside.txt"), "inside");

        await File.WriteAllTextAsync(Path.Combine(sibling, "outside.txt"), "outside");

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "proj", root, enabled: true);

        InMemorySanctumBreachRepository breaches = new();

        SanctumGuard guard = new(
            repository,
            breaches,
            NullLogger<SanctumGuard>.Instance,
            UnregisteredCampaignRoots.Instance,
            new FakeDnsResolver());

        string campaignId = registered.Id.ToString("D");

        Assert.True((await guard.ValidatePathAsync(campaignId, Path.Combine(root, "inside.txt"), "read", "read_file_chunk")).Allowed);

        SanctumResult refused = await guard.ValidatePathAsync(campaignId, Path.Combine(sibling, "outside.txt"), "read", "read_file_chunk");

        Assert.False(refused.Allowed);

        _ = Assert.Single(breaches.Records);
    }

    private void RequireVolume() =>
        Skip.If(
            !GrimoireFixture.SqlCipherAvailable || _volume.Volume is null,
            "A case-sensitive volume could not be attached on this host.");

    private ICampaignRepository CreateRepository() =>
        new CampaignRepository(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

    private static async Task<Campaign> AddAsync(ICampaignRepository repository, string name, string path, bool enabled)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Result<Campaign> added = await repository.AddAsync(
            new Campaign
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = path,
                Type = WorkspaceType.Campaign,
                Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(
                    new SanctumConfig
                    {
                        Enabled = enabled,
                        Mode = SanctumMode.Strict,
                        EnforcePathBoundary = true,
                    }),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Code : null);

        return added.Value;
    }
}

/// <summary>One case-sensitive volume shared by every test of a class; <see cref="Volume"/> is <see langword="null"/> where none can be attached.</summary>
public sealed class CaseSensitiveVolumeFixture : IAsyncLifetime
{
    internal CaseSensitiveVolume? Volume { get; private set; }

    public async Task InitializeAsync() => Volume = await CaseSensitiveVolume.TryCreateAsync();

    public async Task DisposeAsync()
    {
        if (Volume is not null)
        {
            await Volume.DisposeAsync();
        }
    }
}
