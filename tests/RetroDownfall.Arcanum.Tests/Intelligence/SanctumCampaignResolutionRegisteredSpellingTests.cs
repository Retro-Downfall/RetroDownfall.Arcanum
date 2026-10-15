using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
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

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The Sanctum resolution as production composes it: the production canonical resolver over an installation that has registered no Campaign
/// root identity, and the real Campaign table. The Command Center and <c>arcanum run</c> send the physical working directory the operating
/// system reports, and the operator registered the root under whatever spelling they typed.
/// </summary>
[Collection("Grimoire")]
public sealed class SanctumCampaignResolutionRegisteredSpellingTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _parent = string.Empty;

    private ArcanumDbContext? _db;

    public SanctumCampaignResolutionRegisteredSpellingTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _parent = Directory.CreateTempSubdirectory("arcanum-resolution-spelling-").FullName;

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

        _ = TestDirectoryCleanup.TryDelete(_parent, nameof(SanctumCampaignResolutionRegisteredSpellingTests));
    }

    [SkippableTheory]
    [InlineData("root")]
    [InlineData("subdirectory")]
    public async Task A_root_registered_through_a_symbolic_link_governs_a_turn_that_runs_in_its_physical_directory(string where)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        Assert.True(WorkspacePathPolicy.TryCanonicalize(_parent, out string? canonicalParent, out _));

        string physicalRoot = Directory.CreateDirectory(Path.Combine(canonicalParent, "real", "proj")).FullName;

        string physicalSrc = Directory.CreateDirectory(Path.Combine(physicalRoot, "src")).FullName;

        Directory.CreateSymbolicLink(Path.Combine(_parent, "link"), Path.Combine(canonicalParent, "real"));

        CampaignRepository repository = CreateRepository();

        Campaign strict = await RegisterAsync(repository, Path.Combine(_parent, "link", "proj"), enabled: true);

        // No Campaign root identity is registered, as in production: a directory-only turn is canonically bound to no Campaign.
        ICanonicalCampaignContextResolver resolver = ProductionCampaignResolver.Over(new StubSessionBindings(), new AvailableCampaigns(strict.Id));

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, null, where == "root" ? physicalRoot : physicalSrc, null, resolver, repository, CancellationToken.None);

        Assert.Equal(strict.Id, resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);

        Assert.Equal(SanctumStrictness.Strict, CampaignRepository.GetSanctumStrictness(resolved.Loaded!));
    }

    [SkippableFact]
    public async Task A_directory_no_registered_root_contains_stays_unrestricted()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "registered")).FullName;

        string sibling = Directory.CreateDirectory(Path.Combine(_parent, "registered-legacy")).FullName;

        CampaignRepository repository = CreateRepository();

        Campaign strict = await RegisterAsync(repository, root, enabled: true);

        ICanonicalCampaignContextResolver resolver = ProductionCampaignResolver.Over(new StubSessionBindings(), new AvailableCampaigns(strict.Id));

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            null, null, sibling, null, resolver, repository, CancellationToken.None);

        Assert.Null(resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);
    }

    // The production refusal: a Session bound to a Campaign that also names a directory the Campaign does not contain.
    [SkippableFact]
    public async Task A_session_bound_to_a_strict_campaign_governs_a_turn_that_runs_in_another_campaign_with_the_sanctum_off()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string strictRoot = Directory.CreateDirectory(Path.Combine(_parent, "strict")).FullName;

        string openRoot = Directory.CreateDirectory(Path.Combine(_parent, "open")).FullName;

        string openSrc = Directory.CreateDirectory(Path.Combine(openRoot, "src")).FullName;

        CampaignRepository repository = CreateRepository();

        Campaign strict = await RegisterAsync(repository, strictRoot, enabled: true);

        Campaign open = await RegisterAsync(repository, openRoot, enabled: false);

        Guid session = Guid.NewGuid();

        ICanonicalCampaignContextResolver resolver = ProductionCampaignResolver.Over(
            new StubSessionBindings().Bind(session, strict.Id),
            new AvailableCampaigns(strict.Id, open.Id));

        Result<CanonicalCampaignContext> canonical = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(session, null, openSrc),
            CancellationToken.None);

        Assert.True(canonical.IsFailure);

        SanctumCampaignResolution.Resolved resolved = await SanctumCampaignResolution.ResolveAsync(
            session, null, openSrc, null, resolver, repository, CancellationToken.None);

        Assert.Equal(strict.Id, resolved.CampaignId);

        Assert.Equal(SanctumStrictness.Strict, CampaignRepository.GetSanctumStrictness(resolved.Loaded!));
    }

    private CampaignRepository CreateRepository() =>
        new(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            FixtureLabeledArtifactGuard.For(_db!));

    private static async Task<Campaign> RegisterAsync(CampaignRepository repository, string path, bool enabled)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Result<Campaign> added = await repository.AddAsync(
            new Campaign
            {
                Id = Guid.NewGuid(),
                Name = "resolution-" + Guid.NewGuid().ToString("N")[..8],
                Path = path,
                Type = WorkspaceType.Campaign,
                Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(new SanctumConfig { Enabled = enabled }),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Code : null);

        return added.Value;
    }
}
