using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The path boundary of a Strict or audit-only Sanctum is a property of the Campaign's directory, not of the spelling a path reaches it
/// by. A turn is governed by its Campaign whether the working directory is spelled with a different case or Unicode normalisation form,
/// through a symbolic link, or physically, so a path inside the Campaign must be admitted through each of those spellings with no breach
/// recorded, and a path that leaves it must stay refused through each of them.
/// </summary>
[Collection("Grimoire")]
public sealed class SanctumGuardPathSpellingTests : IAsyncLifetime
{
    // Written out as scalar values: string.Normalize answers through the globalization library, which the shipping executable runs without.
    private const string RootName = "Caf\u00e9";

    private const string DecomposedRootName = "Cafe\u0301";

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _parent = string.Empty;

    private ArcanumDbContext? _db;

    public SanctumGuardPathSpellingTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _parent = Directory.CreateTempSubdirectory("arcanum-guard-path-spelling-").FullName;

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

        _ = TestDirectoryCleanup.TryDelete(_parent, nameof(SanctumGuardPathSpellingTests));
    }

    // The same directory under every spelling a working directory can reach it by: the registered spelling, the physical one the
    // operating system reports (macOS puts temporary directories behind the /var link), a differently cased one, the decomposed Unicode
    // one, and a symbolic link into it.
    [SkippableTheory]
    [InlineData("exact", true)]
    [InlineData("physical", true)]
    [InlineData("case", true)]
    [InlineData("decomposed", true)]
    [InlineData("alias", true)]
    [InlineData("exact", false)]
    [InlineData("physical", false)]
    [InlineData("case", false)]
    [InlineData("decomposed", false)]
    [InlineData("alias", false)]
    public async Task A_path_inside_the_campaign_is_admitted_through_every_spelling_of_its_root_and_records_no_breach(
        string spelling,
        bool strict)
    {
        Layout layout = RequireLayout(spelling);

        InMemorySanctumBreachRepository breaches = new();

        SanctumGuard guard = CreateGuard(breaches);

        Campaign campaign = await RegisterAsync(layout.RegisteredRoot, strict ? SanctumMode.Strict : SanctumMode.AuditOnly);

        string campaignId = campaign.Id.ToString("D");

        string[] inside =
        [
            layout.Spelled,
            Path.Combine(layout.Spelled, "notes.txt"),
            Path.Combine(layout.Spelled, "src", "main.txt"),
            Path.Combine(layout.Spelled, "src", "created", "later", "file.txt"),
            Path.Combine(layout.Spelled, "src", "..", "notes.txt"),
        ];

        foreach (string path in inside)
        {
            SanctumResult result = await guard.ValidatePathAsync(campaignId, path, "read", "read_file_chunk");

            Assert.True(result.Allowed, $"'{path}' is inside the Campaign but was refused: {result.DenyReason}");
        }

        Assert.Empty(breaches.Records);
    }

    // The negative twin: a spelling that reaches the root must not become a way out of it.
    [SkippableTheory]
    [InlineData("exact")]
    [InlineData("physical")]
    [InlineData("case")]
    [InlineData("decomposed")]
    [InlineData("alias")]
    public async Task A_path_that_leaves_the_campaign_stays_refused_through_every_spelling_of_its_root(string spelling)
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");

        Layout layout = RequireLayout(spelling);

        InMemorySanctumBreachRepository breaches = new();

        SanctumGuard guard = CreateGuard(breaches);

        Campaign campaign = await RegisterAsync(layout.RegisteredRoot, SanctumMode.Strict);

        string campaignId = campaign.Id.ToString("D");

        // A symbolic link inside the Campaign that leads out of it, a sibling that shares the name prefix, the parent directory, and a
        // relative path that climbs out.
        string[] outside =
        [
            Path.Combine(layout.Spelled, "escape", "secret.txt"),
            Path.Combine(layout.Spelled, "escape"),
            Path.Combine(layout.SpelledParent, RootName + "-legacy", "file.txt"),
            layout.SpelledParent,
            Path.Combine(layout.Spelled, "..", "outside", "secret.txt"),
            Path.Combine(layout.Spelled, "src", "..", "..", "outside", "secret.txt"),
        ];

        foreach (string path in outside)
        {
            SanctumResult result = await guard.ValidatePathAsync(campaignId, path, "read", "read_file_chunk");

            Assert.False(result.Allowed, $"'{path}' leaves the Campaign but was admitted.");

            Assert.Equal(SanctumDenialKind.Policy, result.DenialKind);
        }

        Assert.Equal(outside.Length, breaches.Records.Count);
    }

    [SkippableTheory]
    [InlineData("physical")]
    [InlineData("case")]
    [InlineData("alias")]
    public async Task An_allowed_path_is_matched_by_its_directory_through_every_spelling_too(string spelling)
    {
        Layout layout = RequireLayout(spelling);

        string shared = Directory.CreateDirectory(Path.Combine(_parent, "shared")).FullName;

        await File.WriteAllTextAsync(Path.Combine(shared, "data.txt"), "shared");

        string outsideTarget = Directory.CreateDirectory(Path.Combine(_parent, "shared-out")).FullName;

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(shared, "escape"), outsideTarget);
        }

        string sharedSpelled = spelling switch
        {
            "physical" => PhysicalTestPaths.Resolve(shared),
            "case" => Path.Combine(_parent, "SHARED"),
            _ => LinkTo(shared, "shared-alias"),
        };

        Skip.IfNot(Directory.Exists(sharedSpelled), "The volume does not treat the spelling as the same directory.");

        InMemorySanctumBreachRepository breaches = new();

        SanctumGuard guard = CreateGuard(breaches);

        // The allowed directory is registered under its plain spelling; a path reaches it through another.
        Campaign campaign = await RegisterAsync(layout.RegisteredRoot, SanctumMode.Strict, allowedPaths: [shared]);

        string campaignId = campaign.Id.ToString("D");

        Assert.True(
            (await guard.ValidatePathAsync(campaignId, Path.Combine(sharedSpelled, "data.txt"), "read", "read_file_chunk")).Allowed);

        if (!OperatingSystem.IsWindows())
        {
            Assert.False(
                (await guard.ValidatePathAsync(campaignId, Path.Combine(sharedSpelled, "escape", "x.txt"), "read", "read_file_chunk")).Allowed);
        }

        // Only the attempt to leave through the link out of the allowed directory is a breach.
        Assert.Equal(OperatingSystem.IsWindows() ? 0 : 1, breaches.Records.Count);
    }

    private Layout RequireLayout(string spelling)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(
            spelling is "alias" && OperatingSystem.IsWindows(),
            "Creating a symbolic link needs a privilege the Windows test host does not have.");

        string root = Directory.CreateDirectory(Path.Combine(_parent, RootName)).FullName;

        _ = Directory.CreateDirectory(Path.Combine(root, "src"));

        string outside = Directory.CreateDirectory(Path.Combine(_parent, "outside")).FullName;

        _ = Directory.CreateDirectory(Path.Combine(_parent, RootName + "-legacy"));

        File.WriteAllText(Path.Combine(root, "notes.txt"), "inside");

        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
        }

        string spelled;

        string spelledParent = _parent;

        switch (spelling)
        {
            case "physical":
                spelled = PhysicalTestPaths.Resolve(root);

                spelledParent = PhysicalTestPaths.Resolve(_parent);

                break;

            case "case":
                spelled = Path.Combine(_parent, RootName.ToUpperInvariant());

                break;

            case "decomposed":
                spelled = Path.Combine(_parent, DecomposedRootName);

                break;

            case "alias":
                spelled = LinkTo(root, "alias");

                break;

            default:
                spelled = root;

                break;
        }

        Skip.IfNot(Directory.Exists(spelled), "The volume does not treat the spelling as the same directory.");

        return new Layout(root, spelled, spelledParent);
    }

    private string LinkTo(string target, string name)
    {
        string link = Path.Combine(_parent, name);

        Directory.CreateSymbolicLink(link, target);

        return link;
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

    private async Task<Campaign> RegisterAsync(string path, SanctumMode mode, IReadOnlyList<string>? allowedPaths = null)
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
                        Mode = mode,
                        EnforcePathBoundary = true,
                        AllowedPaths = allowedPaths ?? [],
                    }),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Code : null);

        return added.Value;
    }

    /// <summary>The directory as registered, the spelling a path reaches it by, and that spelling's parent.</summary>
    private sealed record Layout(string RegisteredRoot, string Spelled, string SpelledParent);
}
