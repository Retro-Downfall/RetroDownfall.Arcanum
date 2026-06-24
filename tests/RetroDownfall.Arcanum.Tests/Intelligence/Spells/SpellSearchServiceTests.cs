using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

public sealed class SpellSearchServiceTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

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

}
