using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// <see cref="ICampaignRepository.GetByContainedPathAsync"/> against the real Campaign table: a directory is recognised as a registered
/// Campaign's workspace when the Campaign's root is the directory or one of its ancestors, matched by whole components.
/// </summary>
[Collection("Grimoire")]
public sealed class CampaignRepositoryContainedPathTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _parent = string.Empty;

    private ArcanumDbContext? _db;

    public CampaignRepositoryContainedPathTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _parent = Directory.CreateTempSubdirectory("arcanum-contained-path-").FullName;

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

        _ = TestDirectoryCleanup.TryDelete(_parent, nameof(CampaignRepositoryContainedPathTests));
    }

    [SkippableFact]
    public async Task The_root_itself_and_every_directory_below_it_resolve_to_the_campaign()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "alpha")).FullName;

        string deep = Directory.CreateDirectory(Path.Combine(root, "src", "deep")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign alpha = await AddAsync(repository, "alpha", root);

        Assert.Equal(alpha.Id, (await repository.GetByContainedPathAsync(root))?.Id);

        Assert.Equal(alpha.Id, (await repository.GetByContainedPathAsync(Path.Combine(root, "src")))?.Id);

        Assert.Equal(alpha.Id, (await repository.GetByContainedPathAsync(deep))?.Id);

        // A directory that does not exist yet is still below the root by its spelling.
        Assert.Equal(alpha.Id, (await repository.GetByContainedPathAsync(Path.Combine(root, "not", "created", "yet")))?.Id);
    }

    [SkippableFact]
    public async Task A_trailing_separator_on_either_side_is_ignored()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "trailing")).FullName;

        ICampaignRepository repository = CreateRepository();

        // The registration keeps the spelling it was given, trailing separator included.
        Campaign registered = await AddAsync(repository, "trailing", root + Path.DirectorySeparatorChar);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(root))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(root + Path.DirectorySeparatorChar))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(Path.Combine(root, "src") + Path.DirectorySeparatorChar))?.Id);
    }

    [SkippableFact]
    public async Task A_sibling_sharing_a_name_prefix_is_not_contained()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string app = Directory.CreateDirectory(Path.Combine(_parent, "app")).FullName;

        string legacy = Directory.CreateDirectory(Path.Combine(_parent, "app-legacy")).FullName;

        ICampaignRepository repository = CreateRepository();

        _ = await AddAsync(repository, "app", app);

        Assert.Null(await repository.GetByContainedPathAsync(legacy));

        Assert.Null(await repository.GetByContainedPathAsync(Path.Combine(legacy, "src")));

        Assert.Null(await repository.GetByContainedPathAsync(_parent));
    }

    [SkippableFact]
    public async Task The_deepest_registered_root_wins()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string outer = Directory.CreateDirectory(Path.Combine(_parent, "outer")).FullName;

        string inner = Directory.CreateDirectory(Path.Combine(outer, "inner")).FullName;

        ICampaignRepository repository = CreateRepository();

        Campaign outerCampaign = await AddAsync(repository, "outer", outer);

        Campaign innerCampaign = await AddAsync(repository, "inner", inner);

        Assert.Equal(innerCampaign.Id, (await repository.GetByContainedPathAsync(Path.Combine(inner, "src")))?.Id);

        Assert.Equal(outerCampaign.Id, (await repository.GetByContainedPathAsync(Path.Combine(outer, "docs")))?.Id);
    }

    [SkippableFact]
    public async Task An_unregistered_or_unusable_path_resolves_to_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ICampaignRepository repository = CreateRepository();

        _ = await AddAsync(repository, "only", Directory.CreateDirectory(Path.Combine(_parent, "only")).FullName);

        Assert.Null(await repository.GetByContainedPathAsync(Directory.CreateDirectory(Path.Combine(_parent, "elsewhere")).FullName));

        Assert.Null(await repository.GetByContainedPathAsync("bad\u0000path"));
    }

    [SkippableFact]
    public async Task A_symlinked_ancestor_of_the_working_directory_resolves_to_the_campaign_it_points_into()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        // The registered root is spelled canonically (its own links resolved), the way a path the operating system reports is.
        Assert.True(WorkspacePathPolicy.TryCanonicalize(_parent, out string? canonicalParent, out _));

        string root = Directory.CreateDirectory(Path.Combine(canonicalParent, "real", "proj")).FullName;

        string src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;

        string link = Path.Combine(_parent, "link");

        Directory.CreateSymbolicLink(link, Path.Combine(_parent, "real"));

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "proj", root);

        string throughTheLink = Path.Combine(link, "proj", "src");

        Assert.True(Directory.Exists(throughTheLink));

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(throughTheLink))?.Id);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(src))?.Id);
    }

    [SkippableFact]
    public async Task The_case_of_the_working_directory_does_not_hide_the_campaign_on_a_case_insensitive_platform()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.IfNot(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Only the case-insensitive platforms treat a case variant as the same directory.");

        string root = Directory.CreateDirectory(Path.Combine(_parent, "CasedRoot")).FullName;

        Skip.IfNot(Directory.Exists(Path.Combine(_parent, "casedroot")), "This volume is case sensitive.");

        ICampaignRepository repository = CreateRepository();

        Campaign registered = await AddAsync(repository, "cased", root);

        Assert.Equal(registered.Id, (await repository.GetByContainedPathAsync(Path.Combine(_parent, "CASEDROOT", "src")))?.Id);
    }

    private ICampaignRepository CreateRepository() =>
        new CampaignRepository(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            FixtureLabeledArtifactGuard.For(_db!));

    private static async Task<Campaign> AddAsync(ICampaignRepository repository, string name, string path)
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
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(CampaignRepository.DefaultSanctumConfig()),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Code : null);

        return added.Value;
    }
}
