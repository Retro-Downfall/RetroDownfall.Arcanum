using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
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
/// case-sibling registration does not outrank the real parent, and the two can be registered side by side. That holds for a path read as
/// it is spelled (the attachment source routes) as for a directory read with its links resolved: a case sibling is neither taken for the
/// Campaign a path is inside nor judges a file that is not its own. Every test here needs a real case-sensitive volume and skips where one
/// cannot be attached.
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
    public async Task A_spelled_path_belongs_to_the_root_it_is_spelled_inside_and_not_to_its_case_sibling()
    {
        RequireVolume();

        string lower = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        string upper = Directory.CreateDirectory(Path.Combine(_parent, "Proj")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign strict = await AddAsync(repository, "proj-strict", lower, enabled: true);

        Campaign open = await AddAsync(repository, "proj-open", upper, enabled: false);

        // The fold between the two spellings is not one the volume makes, so neither registration is taken for the other, whichever of
        // them is the stricter and whichever is tried first.
        Assert.Equal(open.Id, (await repository.GetBySpelledPathAsync(Path.Combine(upper, "notes.txt")))?.Id);

        Assert.Equal(strict.Id, (await repository.GetBySpelledPathAsync(Path.Combine(lower, "notes.txt")))?.Id);

        Assert.Equal(open.Id, (await repository.GetBySpelledPathAsync(Path.Combine(upper, "src", "deep", "notes.txt")))?.Id);

        // A spelling in a third case names no directory on the volume, so the file system has nothing to say about it and the text decides:
        // both roots match it, at the same depth, and the stricter governs.
        Assert.Equal(strict.Id, (await repository.GetBySpelledPathAsync(Path.Combine(_parent, "PROJ", "notes.txt")))?.Id);
    }

    [SkippableFact]
    public async Task A_case_sibling_registration_below_a_strict_parent_does_not_outrank_it_for_a_spelled_path()
    {
        RequireVolume();

        string work = Directory.CreateDirectory(Path.Combine(_parent, "work")).FullName;

        string realChild = Directory.CreateDirectory(Path.Combine(work, "proj")).FullName;

        string sibling = Directory.CreateDirectory(Path.Combine(work, "Proj")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign strict = await AddAsync(repository, "work-strict", work, enabled: true);

        Campaign open = await AddAsync(repository, "sibling-open", sibling, enabled: false);

        // The directory is inside work/proj, which is not work/Proj, so the Campaign that really contains it governs it...
        Assert.Equal(strict.Id, (await repository.GetBySpelledPathAsync(Path.Combine(realChild, "x.txt")))?.Id);

        // ...and the sibling's own tree is governed by the sibling's registration, the deeper of the two roots that contain it.
        Assert.Equal(open.Id, (await repository.GetBySpelledPathAsync(Path.Combine(sibling, "x.txt")))?.Id);
    }

    [SkippableTheory]
    [InlineData("attachment_reference", true)]
    [InlineData("attachment_reference", false)]
    [InlineData("attachment_refresh", true)]
    [InlineData("attachment_refresh", false)]
    public async Task A_live_attachment_source_is_judged_by_the_campaign_it_is_inside_and_not_by_a_strict_case_sibling(
        string surface,
        bool sessionBound)
    {
        RequireVolume();

        string strictRoot = Directory.CreateDirectory(Path.Combine(_parent, "proj")).FullName;

        string openRoot = Directory.CreateDirectory(Path.Combine(_parent, "Proj")).FullName;

        string source = Path.Combine(openRoot, "notes.txt");

        await File.WriteAllTextAsync(source, "inside the Campaign whose Sanctum is off");

        ICampaignRepository repository = CreateRepository();

        Campaign strict = await AddAsync(repository, "proj-strict", strictRoot, enabled: true);

        Campaign open = await AddAsync(repository, "proj-open", openRoot, enabled: false);

        InMemorySanctumBreachRepository breaches = new();

        SanctumGuard guard = new(
            repository,
            breaches,
            NullLogger<SanctumGuard>.Instance,
            UnregisteredCampaignRoots.Instance,
            new FakeDnsResolver());

        Guid? sessionCampaign = sessionBound ? open.Id : null;

        AttachmentSourcePathAuthorizer authorizer = AttachmentSourceSanctum.Authorizer(
            Guid.NewGuid(),
            sessionCampaign,
            surface,
            new FixedCampaignResolver(sessionCampaign),
            repository,
            guard,
            NullLogger.Instance);

        // The file is the open Campaign's, so the strict Campaign's path boundary does not judge it and its history stays empty.
        Assert.True(await authorizer(new AttachmentSourceRead(source, source), CancellationToken.None));

        Assert.Empty(breaches.Records);

        // The strict Campaign still governs its own tree: a source spelled inside it that resolves outside its root is refused and recorded.
        string outside = Path.Combine(_parent, "outside.txt");

        await File.WriteAllTextAsync(outside, "outside every root");

        Assert.False(
            await authorizer(new AttachmentSourceRead(Path.Combine(strictRoot, "notes.txt"), outside), CancellationToken.None));

        Assert.Equal(strict.Id, Guid.Parse(Assert.Single(breaches.Records).CampaignId));
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
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            FixtureLabeledArtifactGuard.For(_db!));

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
