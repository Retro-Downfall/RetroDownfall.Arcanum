using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Platform;
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

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The guard's lookups by workspace directory against the real Campaign table: a root registered under one spelling (through a symbolic
/// link, or through a differently cased or differently normalised name) governs the same directory when the operating system reports it
/// under another, because a Sanctum that does not apply to the directory a tool runs in contains nothing.
/// </summary>
[Collection("WorkspacePathPolicy")]
public sealed class SanctumGuardRegisteredSpellingTests : IClassFixture<GrimoireFixture>, IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _parent = string.Empty;

    private ArcanumDbContext? _db;

    public SanctumGuardRegisteredSpellingTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _parent = Directory.CreateTempSubdirectory("arcanum-guard-spelling-").FullName;

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

        _ = TestDirectoryCleanup.TryDelete(_parent, nameof(SanctumGuardRegisteredSpellingTests));
    }

    [SkippableFact]
    public async Task A_root_registered_through_a_symbolic_link_governs_the_physical_workspace_below_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string canonicalParent = PhysicalTestPaths.Resolve(_parent);

        string physicalRoot = Directory.CreateDirectory(Path.Combine(canonicalParent, "real", "proj")).FullName;

        string physicalSrc = Directory.CreateDirectory(Path.Combine(physicalRoot, "src")).FullName;

        Directory.CreateSymbolicLink(Path.Combine(_parent, "link"), Path.Combine(canonicalParent, "real"));

        Campaign registered = await RegisterAsync(Path.Combine(_parent, "link", "proj"), maxFileWriteMb: 7);

        InMemorySanctumBreachRepository breaches = new();

        SanctumGuard guard = CreateGuard(breaches);

        Assert.Equal(7, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync(physicalSrc)).MaxFileWriteMb);

        SanctumChildProcessBoundary? boundary = await guard.GetChildProcessBoundaryForWorkspaceAsync(physicalSrc);

        Assert.NotNull(boundary);

        Assert.True(boundary.PathBoundaryRequired);

        await guard.RecordResourceLimitBreachAsync(physicalSrc, "execute_command", ResourceLimitKind.Cpu, "30s", "31s");

        SanctumBreachRecord record = Assert.Single(breaches.Records);

        Assert.Equal(registered.Id.ToString("D"), record.CampaignId, ignoreCase: true);
    }

    [SkippableFact]
    public async Task A_canonically_equivalent_unicode_spelling_of_a_root_governs_its_workspace()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string composed = "café";

        string decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);

        string root = Directory.CreateDirectory(Path.Combine(_parent, composed)).FullName;

        Skip.IfNot(Directory.Exists(Path.Combine(_parent, decomposed)), "This volume tells the two spellings apart.");

        _ = Directory.CreateDirectory(Path.Combine(root, "src"));

        _ = await RegisterAsync(root, maxFileWriteMb: 7);

        SanctumGuard guard = CreateGuard(new InMemorySanctumBreachRepository());

        Assert.Equal(7, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync(Path.Combine(_parent, decomposed, "src"))).MaxFileWriteMb);
    }

    [SkippableFact]
    public async Task A_directory_no_registered_root_contains_gets_the_defaults()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = Directory.CreateDirectory(Path.Combine(_parent, "registered")).FullName;

        string sibling = Directory.CreateDirectory(Path.Combine(_parent, "registered-legacy")).FullName;

        _ = await RegisterAsync(root, maxFileWriteMb: 7);

        SanctumGuard guard = CreateGuard(new InMemorySanctumBreachRepository());

        Assert.Equal(100, (await guard.GetEffectiveResourceLimitsForWorkspaceAsync(sibling)).MaxFileWriteMb);

        Assert.Null(await guard.GetChildProcessBoundaryForWorkspaceAsync(sibling));
    }

    private SanctumGuard CreateGuard(ISanctumBreachRepository breaches) =>
        new(
            new CampaignRepository(
                _db!,
                NullLogger<CampaignRepository>.Instance,
                new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
                FixtureLabeledArtifactGuard.For(_db!)),
            breaches,
            NullLogger<SanctumGuard>.Instance,
            UnregisteredCampaignRoots.Instance,
            new FakeDnsResolver());

    private async Task<Campaign> RegisterAsync(string path, int maxFileWriteMb)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        CampaignRepository repository = new(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            FixtureLabeledArtifactGuard.For(_db!));

        Result<Campaign> added = await repository.AddAsync(
            new Campaign
            {
                Id = Guid.NewGuid(),
                Name = "spelling-" + Guid.NewGuid().ToString("N")[..8],
                Path = path,
                Type = WorkspaceType.Campaign,
                Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(
                    new SanctumConfig
                    {
                        Enabled = true,
                        EnforcePathBoundary = true,
                        ResourceLimits = new ResourceLimits(MaxFileWriteMb: maxFileWriteMb),
                    }),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Code : null);

        return added.Value;
    }
}
