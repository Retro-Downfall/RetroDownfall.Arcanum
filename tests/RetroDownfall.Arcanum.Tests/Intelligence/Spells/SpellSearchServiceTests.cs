using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

[Collection("ProcessEnvironment")]
public sealed class SpellSearchServiceTests : IAsyncLifetime
{
    private TempWorkspace _workspace = null!;

    private string _testHome = string.Empty;

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public async Task InitializeAsync()
    {
        _testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"spell-search-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testHome);

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile(
            "spells/scout/SPELL.md",
            """
            ---
            name: scout
            description: Looks ahead
            tags: [explore]
            tools: list_directory
            ---
            Scout ahead.
            """);

        _workspace.WriteFile(
            "spells/guard/SPELL.md",
            """
            ---
            name: guard
            description: Holds the line
            tags: [defense]
            tools: [read_file]
            ---
            Stand guard.
            """);
    }

    public async Task DisposeAsync()
    {
        await _workspace.DisposeAsync();

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {
            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }

        if (Directory.Exists(_testHome))
        {
            Directory.Delete(_testHome, recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_repeated_search_reuses_selected_campaign_metadata()
    {
        SpellSearchService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        Campaign selected = new() { Id = Guid.NewGuid(), Path = _workspace.Root };

        var campaigns = new List<Campaign> { selected };

        for (int i = 0; i < 19; i++)
        {
            string excludedRoot = Path.Combine(_testHome, "campaign-" + i);

            Directory.CreateDirectory(excludedRoot);

            await File.WriteAllTextAsync(Path.Combine(excludedRoot, "SPELL.md"), $"---\nname: excluded-{i}\n---\nbody");

            campaigns.Add(new Campaign { Id = Guid.NewGuid(), Path = excludedRoot });
        }

        SpellSearchQuery query = new(null, null, null, SpellSource.Campaign, selected.Id, null, campaigns);

        Assert.Equal(2, (await service.SearchAsync(query, CancellationToken.None)).Length);

        int opens = 0;

        global::RetroDownfall.Arcanum.Infrastructure.Security.SecureFileReader.AfterRegularFileOpenedForTests = path =>
        {
            if (path.StartsWith(_workspace.Root, StringComparison.Ordinal)
                || path.StartsWith(_testHome, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref opens);
            }
        };

        try
        {
            Assert.Equal(2, (await service.SearchAsync(query, CancellationToken.None)).Length);

            Assert.Equal(0, opens);
        }
        finally
        {
            global::RetroDownfall.Arcanum.Infrastructure.Security.SecureFileReader.AfterRegularFileOpenedForTests = null;
        }
    }

    [Fact]
    public async Task SearchAsync_filters_by_query_tag_and_tool()
    {
        SpellSearchService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        SpellSearchQuery query = new(
            Query: "scout",
            Tag: null,
            Tool: null,
            Source: SpellSource.Workspace,
            CampaignId: null,
            Workspace: _workspace.Root,
            Campaigns: Array.Empty<Campaign>());

        SpellSummary[] results = await service.SearchAsync(query, CancellationToken.None);

        SpellSummary scout = Assert.Single(results);

        Assert.Equal("scout", scout.Name);

        Assert.Equal(SpellSource.Workspace, scout.Source);
    }

    [Fact]
    public async Task SearchAsync_strips_regex_metacharacters_from_query()
    {
        SpellSearchService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        SpellSearchQuery query = new(
            Query: "sc.out[",
            Tag: null,
            Tool: null,
            Source: SpellSource.Workspace,
            CampaignId: null,
            Workspace: _workspace.Root,
            Campaigns: Array.Empty<Campaign>());

        SpellSummary[] results = await service.SearchAsync(query, CancellationToken.None);

        Assert.Contains(results, r => r.Name == "scout");
    }

    [Fact]
    public async Task SearchAsync_workspace_spell_overrides_builtin_name_collision()
    {
        string builtinRoot = Core.Storage.ArcanumPaths.GlobalSpellsDirectory;

        string? backupDir = null;

        bool createdBuiltinRoot = false;

        try
        {
            if (!Directory.Exists(builtinRoot))
            {
                Directory.CreateDirectory(builtinRoot);

                createdBuiltinRoot = true;
            }

            string builtinSpellDir = Path.Combine(builtinRoot, "scout");

            if (Directory.Exists(builtinSpellDir))
            {
                backupDir = Path.Combine(Path.GetTempPath(), $"arcanum-builtin-backup-{Guid.NewGuid():N}");

                Directory.Move(builtinSpellDir, backupDir);
            }

            Directory.CreateDirectory(builtinSpellDir);

            File.WriteAllText(
                Path.Combine(builtinSpellDir, "SPELL.md"),
                """
                ---
                name: scout
                description: Builtin scout
                ---
                Builtin body.
                """);

            SpellSearchService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            SpellSearchQuery query = new(
                Query: "scout",
                Tag: null,
                Tool: null,
                Source: null,
                CampaignId: null,
                Workspace: _workspace.Root,
                Campaigns: Array.Empty<Campaign>());

            SpellSummary[] results = await service.SearchAsync(query, CancellationToken.None);

            SpellSummary scout = Assert.Single(results, r => r.Name == "scout");

            Assert.Equal(SpellSource.Workspace, scout.Source);

            Assert.Equal("Looks ahead", scout.Description);
        }
        finally
        {
            string builtinSpellDir = Path.Combine(builtinRoot, "scout");

            if (Directory.Exists(builtinSpellDir))
            {
                Directory.Delete(builtinSpellDir, recursive: true);
            }

            if (backupDir is not null && Directory.Exists(backupDir))
            {
                Directory.Move(backupDir, builtinSpellDir);
            }

            if (createdBuiltinRoot && Directory.Exists(builtinRoot) && !Directory.EnumerateFileSystemEntries(builtinRoot).Any())
            {
                Directory.Delete(builtinRoot);
            }
        }
    }

    [Fact]
    public async Task SearchAsync_returns_matches_beyond_the_former_total_result_ceiling()
    {
        const int addedSpellCount = 1001;

        for (int index = 0; index < addedSpellCount; index++)
        {
            _workspace.WriteFile(
                $"spells/bulk-{index:D4}/SPELL.md",
                $"""
                ---
                name: bulk-{index:D4}
                description: bulk spell {index}
                ---
                body
                """);
        }

        SpellSearchService service = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        SpellSearchQuery query = new(
            Query: null,
            Tag: null,
            Tool: null,
            Source: SpellSource.Workspace,
            CampaignId: null,
            Workspace: _workspace.Root,
            Campaigns: Array.Empty<Campaign>());

        SpellSummary[] results = await service.SearchAsync(query, CancellationToken.None);

        Assert.Equal(addedSpellCount + 2, results.Length);

        Assert.Contains(results, static result => result.Name == "bulk-1000");
    }

    private void SetEnvironment(string name, string value)
    {
        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);
    }
}
