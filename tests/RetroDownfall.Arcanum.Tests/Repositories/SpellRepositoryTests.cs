using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Cli.CommandCenter;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("ProcessEnvironment")]
public sealed class SpellRepositoryTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _workspaceRoot = string.Empty;

    private ArcanumDbContext? _db;

    public SpellRepositoryTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _workspaceRoot = Path.Combine(Path.GetTempPath(), "arcanum-spell-repo", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_workspaceRoot);

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

        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [SkippableFact]
    public async Task CreateAsync_GetAsync_ListAsync_and_DeleteAsync_round_trip_workspace_spell()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "round-trip",
            "Round trip spell",
            ["utility"],
            "You are helpful.",
            null,
            null,
            null,
            [],
            [],
            Body: "Cast the spell.");

        Result createResult = await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None);

        Assert.True(createResult.IsSuccess);

        SpellDetail? detail = await repository.GetAsync("round-trip", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(detail);

        Assert.Equal(SpellSource.Workspace, detail!.Source);

        Assert.Equal("Cast the spell.", detail!.Body?.Trim());

        SpellSummary[] listed = await repository.ListAsync(_workspaceRoot, CancellationToken.None);

        Assert.Contains(listed, s => string.Equals(s.Name, "round-trip", StringComparison.OrdinalIgnoreCase));

        Result deleteResult = await repository.DeleteAsync("round-trip", _workspaceRoot, CancellationToken.None);

        Assert.True(deleteResult.IsSuccess);

        Assert.Null(await repository.GetAsync("round-trip", _workspaceRoot, CancellationToken.None));
    }

    [SkippableFact]
    public async Task CreateAsync_with_structured_fields_writes_canonical_SPELL_json()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "structured-create",
            "Structured",
            ["t"],
            null,
            null,
            null,
            null,
            [],
            [],
            Version: "1.2.3",
            DeclaredTools: ["get_local_system_time"],
            Dependencies: []);

        Assert.True((await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None)).IsSuccess);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "structured-create");

        Assert.True(File.Exists(Path.Combine(spellDir, "SPELL.json")));

        Assert.False(File.Exists(Path.Combine(spellDir, "SKILL.json")));

        SpellDetail? detail = await repository.GetAsync("structured-create", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(detail);

        Assert.Equal("1.2.3", detail!.Version);

        Assert.Contains("get_local_system_time", detail.DeclaredTools ?? []);
    }

    [SkippableFact]
    public async Task UpdateAsync_after_legacy_SKILL_json_writes_canonical_SPELL_json()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "legacy-update");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: legacy-update
            description: legacy sidecar
            ---
            body
            """);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SKILL.json"),
            """
            {
              "name": "legacy-update",
              "version": "1.0.0",
              "description": "legacy sidecar",
              "tags": [],
              "declaredTools": ["tool-a"],
              "dependencies": []
            }
            """);

        SpellRepository repository = CreateRepository();

        UpdateSpellRequest update = new(
            Description: "updated",
            Tags: null,
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: null,
            RequiredMcpServers: null,
            Version: "1.1.0",
            DeclaredTools: ["tool-a", "tool-b"]);

        Assert.True((await repository.UpdateAsync("legacy-update", _workspaceRoot, update, CancellationToken.None)).IsSuccess);

        Assert.True(File.Exists(Path.Combine(spellDir, "SPELL.json")));

        string canonical = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.json"));

        Assert.Contains("1.1.0", canonical, StringComparison.Ordinal);

        Assert.Contains("tool-b", canonical, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ActivateVersionAsync_after_legacy_SKILL_json_writes_canonical_SPELL_json()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "legacy-activate");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: legacy-activate
            description: activate test
            ---
            active body
            """);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.v1.0.md"),
            """
            ---
            name: legacy-activate
            description: activate test
            ---
            version body
            """);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SKILL.json"),
            """
            {
              "name": "legacy-activate",
              "version": "1.0.0",
              "description": "activate test",
              "tags": ["keep-me"],
              "declaredTools": ["tool-keep"],
              "dependencies": ["dep-keep"]
            }
            """);

        SpellRepository repository = CreateRepository();

        Result<SpellVersionDto> activated = await repository.ActivateVersionAsync(
            "legacy-activate",
            "1.0",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(activated.IsSuccess);

        Assert.True(File.Exists(Path.Combine(spellDir, "SPELL.json")));

        string canonical = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.json"));

        Assert.Contains("\"activeVersion\"", canonical, StringComparison.Ordinal);

        Assert.Contains("tool-keep", canonical, StringComparison.Ordinal);

        Assert.Contains("dep-keep", canonical, StringComparison.Ordinal);

        Assert.Contains("keep-me", canonical, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ActivateVersionAsync_reactivating_the_active_version_preserves_the_archived_snapshot()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "self-activate");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: self-activate
            description: self activate test
            ---
            drifted working copy
            """);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.v1.0.md"),
            """
            ---
            name: self-activate
            description: self activate test
            ---
            pristine archived body
            """);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.json"),
            """
            {
              "name": "self-activate",
              "version": "1.0.0",
              "description": "self activate test",
              "tags": [],
              "declaredTools": [],
              "dependencies": [],
              "activeVersion": "1.0"
            }
            """);

        SpellRepository repository = CreateRepository();

        Result<SpellVersionDto> activated = await repository.ActivateVersionAsync(
            "self-activate",
            "1.0",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(activated.IsSuccess);

        string archived = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.v1.0.md"));

        Assert.Contains("pristine archived body", archived, StringComparison.Ordinal);

        Assert.DoesNotContain("drifted working copy", archived, StringComparison.Ordinal);

        string active = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md"));

        Assert.Contains("pristine archived body", active, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task CreateAsync_without_workspace_returns_NoWorkspace_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "orphan",
            null,
            [],
            null,
            null,
            null,
            null,
            [],
            [],
            Body: "body");

        Result result = await repository.CreateAsync(null, create, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.NoWorkspace", result.Error.Code);
    }

    [SkippableFact]
    public async Task CreateAsync_with_invalid_name_returns_InvalidName_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "bad name!",
            null,
            [],
            null,
            null,
            null,
            null,
            [],
            [],
            Body: "body");

        Result result = await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.InvalidName", result.Error.Code);
    }

    [SkippableFact]
    public async Task CreateAsync_duplicate_workspace_spell_returns_DuplicateName_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "duplicate",
            null,
            [],
            null,
            null,
            null,
            null,
            [],
            [],
            Body: "first");

        Assert.True((await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None)).IsSuccess);

        Result second = await repository.CreateAsync(_workspaceRoot, create with { Body = "second" }, CancellationToken.None);

        Assert.True(second.IsFailure);

        Assert.Equal("Spell.DuplicateName", second.Error.Code);
    }

    [SkippableFact]
    public async Task UpdateAsync_changes_workspace_spell_content()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "editable",
            "Before",
            ["old"],
            "old prompt",
            null,
            null,
            null,
            [],
            [],
            Body: "old body");

        Assert.True((await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None)).IsSuccess);

        UpdateSpellRequest update = new(
            Description: "After",
            Tags: ["new"],
            SystemPrompt: "new prompt",
            Template: null,
            Model: null,
            Provider: null,
            Tools: null,
            RequiredMcpServers: null);

        Result updateResult = await repository.UpdateAsync("editable", _workspaceRoot, update, CancellationToken.None);

        Assert.True(updateResult.IsSuccess);

        SpellDetail? detail = await repository.GetAsync("editable", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(detail);

        Assert.Equal("After", detail!.Description);

        Assert.Equal("new prompt", detail.SystemPrompt);
    }

    [SkippableFact]
    public async Task UpdateAsync_missing_spell_returns_NotFound_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        UpdateSpellRequest update = new(
            Description: "missing",
            Tags: null,
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: null,
            RequiredMcpServers: null);

        Result result = await repository.UpdateAsync("missing", _workspaceRoot, update, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.NotFound", result.Error.Code);
    }

    [SkippableFact]
    public async Task ValidateAsync_reports_missing_dependency_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "needs-dep",
            "Needs dependency",
            [],
            null,
            null,
            null,
            null,
            [],
            [],
            Body: "body",
            Dependencies: ["missing-dep"]);

        Assert.True((await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None)).IsSuccess);

        SpellValidationResultDto validation = await repository.ValidateAsync(
            "needs-dep",
            _workspaceRoot,
            CancellationToken.None);

        Assert.False(validation.IsValid);

        Assert.Contains(validation.Errors, e => e.Contains("missing-dep", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ValidateAsync_warns_when_declared_tool_is_missing_from_mcp()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "tooly");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: tooly
            description: Tool spell
            ---
            body
            """);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SKILL.json"),
            """
            {
              "name": "tooly",
              "version": "1.0.0",
              "description": "Tool spell",
              "tags": [],
              "declaredTools": ["ghost_tool"],
              "dependencies": []
            }
            """);

        SpellRepository repository = CreateRepository(mcp: new FakeMcpConnectionManager());

        SpellValidationResultDto validation = await repository.ValidateAsync(
            "tooly",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

        Assert.Contains(validation.Warnings, w => w.Contains("ghost_tool", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ValidateAsync_recognizes_browse_web_as_a_builtin_tool()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(
            _workspaceRoot,
            "spells",
            "browser");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: browser
            description: Browser spell
            ---
            body
            """);
        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SKILL.json"),
            """
            {
              "name": "browser",
              "version": "1.0.0",
              "description": "Browser spell",
              "tags": [],
              "declaredTools": ["browse_web"],
              "dependencies": []
            }
            """);

        SpellRepository repository = CreateRepository(
            mcp: new FakeMcpConnectionManager());

        SpellValidationResultDto validation = await repository.ValidateAsync(
            "browser",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        Assert.DoesNotContain(
            validation.Warnings,
            static warning => warning.Contains(
                "browse_web",
                StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task ExportAsync_and_ImportAsync_round_trip_spell_payload()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        string spellContent = """
            ---
            name: export-me
            description: export test
            ---
            Exported body.
            """;

        CreateSpellRequest create = new(
            "export-me",
            "export test",
            ["export"],
            null,
            null,
            null,
            null,
            [],
            [],
            Body: spellContent);

        Assert.True((await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None)).IsSuccess);

        SpellExportDto? exported = await repository.ExportAsync("export-me", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        Assert.Contains("Exported body.", exported!.FullContent, StringComparison.Ordinal);

        string importWorkspace = Path.Combine(_workspaceRoot, "import-target");

        Directory.CreateDirectory(importWorkspace);

        SpellImportRequest import = new(exported, importWorkspace, null);

        Result<SpellSummary> importResult = await repository.ImportAsync(import, CancellationToken.None);

        Assert.True(importResult.IsSuccess);

        Assert.Equal("export-me", importResult.Value!.Name);

        SpellDetail? imported = await repository.GetAsync("export-me", importWorkspace, CancellationToken.None);

        Assert.NotNull(imported);

        Assert.Equal(SpellSource.Workspace, imported!.Source);
    }

    /// <summary>
    /// An export's <c>fullContent</c> is the whole <c>SPELL.md</c>, frontmatter included, and a skipped or absent
    /// sidecar leaves <c>metadata</c> null. Import must read the frontmatter back out of that text rather than
    /// writing it as the new spell's body, or the round trip nests the old frontmatter inside the prompt and drops
    /// the system prompt, template, tools and required MCP servers it declared.
    /// </summary>
    [SkippableFact]
    public async Task ImportAsync_of_an_export_without_a_sidecar_keeps_the_frontmatter_out_of_the_body()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "full-frontmatter");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: full-frontmatter
            description: every field
            tags: alpha, beta
            systemPrompt: You are terse.
            template: Answer {{question}}
            model: some-model
            provider: some-provider
            tools: read_file, list_directory
            requiredMcpServers: arcanum
            ---
            The body text.
            """);

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("full-frontmatter", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        Assert.Null(exported!.Metadata);

        string importWorkspace = Path.Combine(_workspaceRoot, "import-target");

        Directory.CreateDirectory(importWorkspace);

        Result<SpellSummary> importResult = await repository.ImportAsync(
            new SpellImportRequest(exported, importWorkspace, null),
            CancellationToken.None);

        Assert.True(importResult.IsSuccess, importResult.IsFailure ? importResult.Error.Message : null);

        SpellDetail? imported = await repository.GetAsync("full-frontmatter", importWorkspace, CancellationToken.None);

        Assert.NotNull(imported);

        Assert.Equal("every field", imported!.Description);

        Assert.Equal(["alpha", "beta"], imported.Tags);

        Assert.Equal("You are terse.", imported.SystemPrompt);

        Assert.Equal("Answer {{question}}", imported.Template);

        Assert.Equal("some-model", imported.Model);

        Assert.Equal("some-provider", imported.Provider);

        Assert.Equal(["read_file", "list_directory"], imported.Tools);

        Assert.Equal(["arcanum"], imported.RequiredMcpServers);

        Assert.Equal("The body text.", imported.Body?.Trim());

        string written = await File.ReadAllTextAsync(Path.Combine(importWorkspace, "spells", "full-frontmatter", "SPELL.md"));

        Assert.Equal(2, written.Split('\n').Count(static line => line.Trim() == "---"));
    }

    [SkippableFact]
    public async Task SearchAsync_lists_spell_from_workspace_query()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string campaignDir = Path.Combine(_workspaceRoot, "search-campaign");

        Directory.CreateDirectory(campaignDir);

        string spellsDir = Path.Combine(campaignDir, "spells", "searchable");

        Directory.CreateDirectory(spellsDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellsDir, "SPELL.md"),
            """
            ---
            name: searchable
            description: searchable spell
            tags: [search]
            ---
            Search body.
            """);

        SpellRepository repository = CreateRepository();

        SpellSearchQuery query = new(
            Query: "searchable",
            Tag: null,
            Tool: null,
            Source: null,
            CampaignId: null,
            Workspace: campaignDir,
            Campaigns: []);

        SpellSummary[] results = await repository.SearchAsync(query, CancellationToken.None);

        Assert.Contains(results, s => string.Equals(s.Name, "searchable", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task GetAsync_with_blank_name_returns_null()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        SpellDetail? detail = await repository.GetAsync("   ", _workspaceRoot, CancellationToken.None);

        Assert.Null(detail);
    }

    [SkippableFact]
    public async Task DeleteAsync_without_workspace_returns_NoWorkspace_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        Result result = await repository.DeleteAsync("any", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.NoWorkspace", result.Error.Code);
    }

    [SkippableFact]
    public async Task UpdateAsync_without_workspace_returns_NoWorkspace_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        UpdateSpellRequest update = new(
            Description: "x",
            Tags: null,
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: null,
            RequiredMcpServers: null);

        Result result = await repository.UpdateAsync("any", null, update, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.NoWorkspace", result.Error.Code);
    }

    [SkippableFact]
    public async Task CreateAsync_invalid_frontmatter_returns_InvalidFrontmatter_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "frontmatter-bad",
            "bad\ndescription",
            [],
            null,
            null,
            null,
            null,
            [],
            [],
            Body: "body");

        Result result = await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.InvalidFrontmatter", result.Error.Code);
    }

    [SkippableFact]
    public async Task ImportAsync_existing_workspace_spell_returns_NameCollision_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "collision",
            "first",
            [],
            null,
            null,
            null,
            null,
            [],
            [],
            Body: "body");

        Assert.True((await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None)).IsSuccess);

        SpellExportDto payload = new(
            null,
            """
            ---
            name: collision
            description: imported
            ---
            imported body
            """,
            []);

        SpellImportRequest import = new(payload, _workspaceRoot, null);

        Result<SpellSummary> result = await repository.ImportAsync(import, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.NameCollision", result.Error.Code);
    }

    /// <summary>
    /// A workspace spell shadows a built-in one of the same name, so import has to refuse that name exactly
    /// as create does rather than let a bundle replace a built-in spell's behavior.
    /// </summary>
    [SkippableFact]
    public async Task ImportAsync_rejects_a_name_that_matches_a_builtin()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumTestHomeScope home = new("arcanum-spell-import-builtin");

        string builtinDir = Path.Combine(ArcanumPaths.GlobalSpellsDirectory, "builtin-import");

        Directory.CreateDirectory(builtinDir);

        await File.WriteAllTextAsync(
            Path.Combine(builtinDir, "SPELL.md"),
            """
            ---
            name: builtin-import
            description: builtin
            ---
            builtin body
            """);

        SpellRepository repository = CreateRepository();

        SpellExportDto payload = new(
            null,
            """
            ---
            name: builtin-import
            description: impostor
            ---
            impostor body
            """,
            []);

        Result<SpellSummary> result = await repository.ImportAsync(
            new SpellImportRequest(payload, _workspaceRoot, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.NameCollision", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(_workspaceRoot, "spells", "builtin-import")));
    }

    /// <summary>
    /// Import takes back no more than export would emit: a script over the per-file cap, scripts that together
    /// pass the aggregate cap, more scripts than a bundle may carry, and a spell file over the per-file cap
    /// are all refused as an invalid body before anything is written.
    /// </summary>
    [SkippableTheory]
    [InlineData("script")]
    [InlineData("aggregate")]
    [InlineData("count")]
    [InlineData("content")]
    public async Task ImportAsync_oversized_script_returns_validation_error(string shape)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes();

        long aggregateCap = ArcanumSettingClamps.MaxFileReadSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes);

        string content =
            """
            ---
            name: too-big
            description: oversized import
            ---
            body
            """;

        List<SpellExportScriptDto> scripts = [];

        switch (shape)
        {
            case "script":
                scripts.Add(new SpellExportScriptDto("big.sh", Convert.ToBase64String(new byte[checked((int)perFileCap + 1)])));

                break;

            case "aggregate":
                foreach (int index in Enumerable.Range(0, checked((int)(aggregateCap / perFileCap)) + 1))
                {
                    scripts.Add(new SpellExportScriptDto($"part-{index}.sh", Convert.ToBase64String(new byte[checked((int)perFileCap)])));
                }

                break;

            case "count":
                foreach (int index in Enumerable.Range(0, SpellRepository.MaxSpellScriptCount + 1))
                {
                    scripts.Add(new SpellExportScriptDto($"tiny-{index}.sh", Convert.ToBase64String("x"u8.ToArray())));
                }

                break;

            case "content":
                content += new string('x', checked((int)perFileCap));

                break;
        }

        SpellRepository repository = CreateRepository();

        Result<SpellSummary> result = await repository.ImportAsync(
            new SpellImportRequest(new SpellExportDto(null, content, scripts), _workspaceRoot, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.InvalidBody", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(_workspaceRoot, "spells", "too-big")));
    }

    [SkippableFact]
    public async Task ImportAsync_script_that_is_not_base64_returns_InvalidBody_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        SpellExportDto payload = new(
            null,
            """
            ---
            name: not-base64
            description: malformed script
            ---
            body
            """,
            [new SpellExportScriptDto("run.sh", "this is !!! not base64")]);

        Result<SpellSummary> result = await repository.ImportAsync(
            new SpellImportRequest(payload, _workspaceRoot, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.InvalidBody", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(_workspaceRoot, "spells", "not-base64")));
    }

    /// <summary>
    /// Past the cap the bundle keeps the first scripts by ordinal file name, whatever order the filesystem lists
    /// them in, and names the ones it left out, so a caller can tell the bundle is partial.
    /// </summary>
    [SkippableFact]
    public async Task ExportAsync_keeps_the_first_scripts_by_name_at_the_count_cap_and_names_the_rest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("many-scripts");

        string scriptsDir = Path.Combine(spellDir, "scripts");

        Directory.CreateDirectory(scriptsDir);

        int total = SpellRepository.MaxSpellScriptCount + 5;

        // Highest name first, so a listing that follows creation order meets the names backwards.
        foreach (int index in Enumerable.Range(0, total).Reverse())
        {
            await File.WriteAllBytesAsync(Path.Combine(scriptsDir, $"tiny-{index:D3}.sh"), "x"u8.ToArray());
        }

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("many-scripts", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        string[] expectedKept = Enumerable.Range(0, SpellRepository.MaxSpellScriptCount).Select(index => $"tiny-{index:D3}.sh").ToArray();

        string[] expectedOmitted = Enumerable.Range(SpellRepository.MaxSpellScriptCount, 5).Select(index => $"tiny-{index:D3}.sh").ToArray();

        Assert.Equal(expectedKept, exported!.Scripts.Select(static script => script.FileName).ToArray());

        Assert.Equal(expectedOmitted, exported.OmittedScripts);

        Assert.Equal(expectedOmitted.Length, exported.OmittedScriptCount);
    }

    /// <summary>
    /// <c>omittedScripts</c> lists at most 64 names, so a directory with more unusable files than that answers with
    /// a truncated list. <c>omittedScriptCount</c> is the full count, which is how a caller tells a truncated list
    /// from a complete one rather than concluding the unlisted scripts never existed.
    /// </summary>
    [SkippableFact]
    public async Task ExportAsync_counts_every_omitted_script_when_the_omitted_list_is_truncated()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("crowded-scripts");

        string scriptsDir = Path.Combine(spellDir, "scripts");

        Directory.CreateDirectory(scriptsDir);

        int omitted = SpellRepository.MaxSpellScriptCount + 6;

        foreach (int index in Enumerable.Range(0, SpellRepository.MaxSpellScriptCount + omitted))
        {
            await File.WriteAllBytesAsync(Path.Combine(scriptsDir, $"tiny-{index:D3}.sh"), "x"u8.ToArray());
        }

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("crowded-scripts", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        Assert.Equal(SpellRepository.MaxSpellScriptCount, exported!.Scripts.Count);

        Assert.Equal(SpellRepository.MaxSpellScriptCount, exported.OmittedScripts!.Count);

        Assert.Equal(omitted, exported.OmittedScriptCount);
    }

    [SkippableFact]
    public async Task ExportAsync_reports_no_omitted_scripts_when_the_bundle_is_complete()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("complete-bundle");

        Directory.CreateDirectory(Path.Combine(spellDir, "scripts"));

        await File.WriteAllBytesAsync(Path.Combine(spellDir, "scripts", "only.sh"), "echo ok"u8.ToArray());

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("complete-bundle", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        Assert.Equal("only.sh", Assert.Single(exported!.Scripts).FileName);

        Assert.NotNull(exported.OmittedScripts);

        Assert.Empty(exported.OmittedScripts!);

        Assert.Equal(0, exported.OmittedScriptCount);
    }

    /// <summary>
    /// Each update reads the spell and writes it back, so two that run together and read the same snapshot would
    /// keep only the later write's field. Reading under the write lock makes each build on the last.
    /// </summary>
    [SkippableFact]
    public async Task UpdateAsync_concurrent_field_updates_both_survive()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        foreach (int round in Enumerable.Range(0, 5))
        {
            string name = $"concurrent-{round}";

            Assert.True(
                (await repository.CreateAsync(
                    _workspaceRoot,
                    new CreateSpellRequest(name, "original", ["seed"], null, null, null, null, [], [], Body: "body"),
                    CancellationToken.None)).IsSuccess);

            UpdateSpellRequest[] updates =
            [
                new(Description: "changed-description", Tags: null, SystemPrompt: null, Template: null, Model: null, Provider: null, Tools: null, RequiredMcpServers: null),
                new(Description: null, Tags: ["changed-tag"], SystemPrompt: null, Template: null, Model: null, Provider: null, Tools: null, RequiredMcpServers: null),
                new(Description: null, Tags: null, SystemPrompt: "changed-system-prompt", Template: null, Model: null, Provider: null, Tools: null, RequiredMcpServers: null),
                new(Description: null, Tags: null, SystemPrompt: null, Template: null, Model: "changed-model", Provider: null, Tools: null, RequiredMcpServers: null),
                new(Description: null, Tags: null, SystemPrompt: null, Template: null, Model: null, Provider: "changed-provider", Tools: null, RequiredMcpServers: null),
            ];

            using ManualResetEventSlim start = new(false);

            Task<Result>[] running = updates
                .Select(update => Task.Run(
                    () =>
                    {
                        start.Wait();

                        return repository.UpdateAsync(name, _workspaceRoot, update, CancellationToken.None);
                    }))
                .ToArray();

            start.Set();

            Result[] results = await Task.WhenAll(running);

            Assert.All(results, static result => Assert.True(result.IsSuccess));

            SpellDetail? detail = await repository.GetAsync(name, _workspaceRoot, CancellationToken.None);

            Assert.NotNull(detail);

            Assert.Equal("changed-description", detail!.Description);

            Assert.Contains("changed-tag", detail.Tags);

            Assert.Equal("changed-system-prompt", detail.SystemPrompt);

            Assert.Equal("changed-model", detail.Model);

            Assert.Equal("changed-provider", detail.Provider);
        }
    }

    /// <summary>
    /// The clone is on disk the moment its directory is moved into place, so a caller that cancels after that
    /// is told what happened rather than that the write failed. The summary read-back does not run on the
    /// caller's token, and nothing logs an error for a spell that exists.
    /// </summary>
    [SkippableFact]
    public async Task CloneAsync_cancelled_after_move_reports_the_clone_as_created()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TestCapturingLogger<SpellRepository> logger = new();

        SpellRepository repository = CreateRepository(logger: logger);

        Assert.True(
            (await repository.CreateAsync(
                _workspaceRoot,
                new CreateSpellRequest("clone-source", "source", [], null, null, null, null, [], [], Body: "body"),
                CancellationToken.None)).IsSuccess);

        using CancellationTokenSource callerAborted = new();

        repository.AfterSpellDirectoryPublishedForTests = callerAborted.Cancel;

        Result<SpellSummary> result = await repository.CloneAsync(
            "clone-source",
            _workspaceRoot,
            new CloneSpellRequest("clone-copy"),
            callerAborted.Token);

        Assert.True(callerAborted.IsCancellationRequested);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);

        Assert.Equal("clone-copy", result.Value!.Name);

        Assert.True(Directory.Exists(Path.Combine(_workspaceRoot, "spells", "clone-copy")));

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    [SkippableFact]
    public async Task ImportAsync_cancelled_after_move_does_not_report_a_write_failure()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TestCapturingLogger<SpellRepository> logger = new();

        SpellRepository repository = CreateRepository(logger: logger);

        using CancellationTokenSource callerAborted = new();

        repository.AfterSpellDirectoryPublishedForTests = callerAborted.Cancel;

        Result<SpellSummary> result = await repository.ImportAsync(
            new SpellImportRequest(ImportPayloadNamed("import-cancelled"), _workspaceRoot, null),
            callerAborted.Token);

        Assert.True(callerAborted.IsCancellationRequested);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);

        Assert.Equal("import-cancelled", result.Value!.Name);

        Assert.True(Directory.Exists(Path.Combine(_workspaceRoot, "spells", "import-cancelled")));

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// Before the spell directory is published a cancelled caller really has stopped the write: the
    /// cancellation propagates, nothing is published, the staging directory is cleaned up, and no error is
    /// logged for it (the blanket catch would have turned it into a write failure and an Error log).
    /// </summary>
    [SkippableTheory]
    [InlineData("create")]
    [InlineData("clone")]
    [InlineData("import")]
    public async Task Staged_write_cancelled_before_the_move_propagates_and_publishes_nothing(string operation)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TestCapturingLogger<SpellRepository> logger = new();

        SpellRepository repository = CreateRepository(logger: logger);

        Assert.True(
            (await repository.CreateAsync(
                _workspaceRoot,
                new CreateSpellRequest("staged-source", "source", [], null, null, null, null, [], [], Body: "body"),
                CancellationToken.None)).IsSuccess);

        using CancellationTokenSource callerAborted = new();

        repository.BeforeFirstSpellWriteForTests = callerAborted.Cancel;

        Task running = operation switch
        {
            "create" => repository.CreateAsync(
                _workspaceRoot,
                new CreateSpellRequest("staged-target", "target", [], null, null, null, null, [], [], Body: "body"),
                callerAborted.Token),
            "clone" => repository.CloneAsync(
                "staged-source",
                _workspaceRoot,
                new CloneSpellRequest("staged-target"),
                callerAborted.Token),
            _ => repository.ImportAsync(
                new SpellImportRequest(ImportPayloadNamed("staged-target"), _workspaceRoot, null),
                callerAborted.Token),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        string spellsRoot = Path.Combine(_workspaceRoot, "spells");

        Assert.False(Directory.Exists(Path.Combine(spellsRoot, "staged-target")));

        Assert.Empty(Directory.GetDirectories(spellsRoot, ".staging-*"));

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// The in-place writers are the same: cancelled before the first replace, the cancellation propagates, the
    /// file the call was about to replace is untouched, nothing is left beside it, and no error is logged.
    /// </summary>
    [SkippableTheory]
    [InlineData("update")]
    [InlineData("create-version")]
    [InlineData("update-version")]
    public async Task In_place_write_cancelled_before_the_replace_propagates_and_changes_nothing(string operation)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("in-place-cancel");

        string versionFile = Path.Combine(spellDir, "SPELL.v2.0.md");

        string versionOriginal = "---\nname: in-place-cancel\ndescription: export fixture\n---\nversion body";

        if (operation == "update-version")
        {
            await File.WriteAllTextAsync(versionFile, versionOriginal);
        }

        string specOriginal = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md"));

        TestCapturingLogger<SpellRepository> logger = new();

        SpellRepository repository = CreateRepository(logger: logger);

        using CancellationTokenSource callerAborted = new();

        repository.BeforeFirstSpellWriteForTests = callerAborted.Cancel;

        Task running = operation switch
        {
            "update" => repository.UpdateAsync(
                "in-place-cancel",
                _workspaceRoot,
                new UpdateSpellRequest("changed", null, null, null, null, null, null, null),
                callerAborted.Token),
            "create-version" => repository.CreateVersionAsync(
                "in-place-cancel",
                _workspaceRoot,
                new CreateSpellVersionRequest("2.0", "new version body"),
                callerAborted.Token),
            _ => repository.UpdateVersionAsync(
                "in-place-cancel",
                "2.0",
                _workspaceRoot,
                new UpdateSpellVersionRequest("changed version body"),
                callerAborted.Token),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(specOriginal, await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md")));

        if (operation == "update-version")
        {
            Assert.Equal(versionOriginal, await File.ReadAllTextAsync(versionFile));
        }
        else
        {
            Assert.False(File.Exists(versionFile));
        }

        Assert.Empty(Directory.GetFiles(spellDir, ".*.tmp"));

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// The spell is on disk when the summary read-back runs, so a read-back that cannot see it answers with the
    /// summary the written content describes rather than failing a write that succeeded.
    /// </summary>
    [SkippableFact]
    public async Task ImportAsync_answers_from_the_written_content_when_the_read_back_cannot_see_the_spell()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TestCapturingLogger<SpellRepository> logger = new();

        SpellRepository repository = CreateRepository(logger: logger);

        string spellFile = Path.Combine(_workspaceRoot, "spells", "unlisted-import", "SPELL.md");

        long oversize = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes() + 1;

        // Once the directory is published, make the catalog skip the file: the read-back then lists nothing.
        repository.AfterSpellDirectoryPublishedForTests = () => File.WriteAllBytes(spellFile, new byte[checked((int)oversize)]);

        SkillMetadata metadata = new(
            "unlisted-import",
            "1.0.0",
            "written but not listed",
            ["alpha", "beta"],
            null,
            null,
            [],
            [],
            null,
            null,
            null,
            null);

        SpellExportDto payload = new(metadata, "body", []);

        Result<SpellSummary> result = await repository.ImportAsync(
            new SpellImportRequest(payload, _workspaceRoot, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : null);

        Assert.Equal("unlisted-import", result.Value!.Name);

        Assert.Equal("written but not listed", result.Value.Description);

        Assert.Equal(SpellSource.Workspace, result.Value.Source);

        Assert.Equal(["alpha", "beta"], result.Value.Tags);

        // The read-back could not do its job, which is worth a Warning, but the write did not fail.
        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Warning);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    [SkippableFact]
    public async Task CloneAsync_answers_from_the_written_content_when_the_read_back_cannot_see_the_spell()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TestCapturingLogger<SpellRepository> logger = new();

        SpellRepository repository = CreateRepository(logger: logger);

        Assert.True(
            (await repository.CreateAsync(
                _workspaceRoot,
                new CreateSpellRequest("unlisted-source", "source description", ["gamma"], null, null, null, null, [], [], Body: "body"),
                CancellationToken.None)).IsSuccess);

        string spellFile = Path.Combine(_workspaceRoot, "spells", "unlisted-clone", "SPELL.md");

        long oversize = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes() + 1;

        repository.AfterSpellDirectoryPublishedForTests = () => File.WriteAllBytes(spellFile, new byte[checked((int)oversize)]);

        Result<SpellSummary> result = await repository.CloneAsync(
            "unlisted-source",
            _workspaceRoot,
            new CloneSpellRequest("unlisted-clone"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : null);

        Assert.Equal("unlisted-clone", result.Value!.Name);

        Assert.Equal("source description", result.Value.Description);

        Assert.Equal(SpellSource.Workspace, result.Value.Source);

        Assert.Equal(["gamma"], result.Value.Tags);

        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Warning);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// A version activation that is cancelled while it is still reading the version file stops there: the
    /// cancellation propagates, SPELL.md is untouched and no backup is written.
    /// </summary>
    [SkippableFact]
    public async Task ActivateVersionAsync_cancelled_while_reading_the_version_file_propagates()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("cancel-activate");

        string versionFile = Path.Combine(spellDir, "SPELL.v2.0.md");

        await File.WriteAllTextAsync(
            versionFile,
            """
            ---
            name: cancel-activate
            description: export fixture
            ---
            version body
            """);

        string original = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md"));

        TestCapturingLogger<SpellRepository> logger = new();

        SpellRepository repository = CreateRepository(logger: logger);

        using CancellationTokenSource callerAborted = new();

        Action<string>? priorSeam = SecureFileReader.AfterOpenForTests;

        // The seam is process-wide, so it reacts only to this test's version file.
        SecureFileReader.AfterOpenForTests = path =>
        {
            if (string.Equals(Path.GetFileName(path), "SPELL.v2.0.md", StringComparison.Ordinal)
                && path.Contains(_workspaceRoot, StringComparison.Ordinal))
            {
                callerAborted.Cancel();
            }
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.ActivateVersionAsync("cancel-activate", "2.0", _workspaceRoot, callerAborted.Token));
        }
        finally
        {
            SecureFileReader.AfterOpenForTests = priorSeam;
        }

        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md")));

        Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// An update writes SPELL.md and then the sidecar that describes it. When the sidecar write fails the caller
    /// is told the update failed, so SPELL.md is put back rather than left describing a spell the sidecar does
    /// not match.
    /// </summary>
    [SkippableFact]
    public async Task UpdateAsync_restores_SPELL_md_when_the_sidecar_write_fails()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("rollback-update");

        string original = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md"));

        // A directory where the sidecar belongs: the scan sees no sidecar, and writing one cannot succeed.
        Directory.CreateDirectory(Path.Combine(spellDir, "SPELL.json"));

        TestCapturingLogger<SpellRepository> logger = new();

        SpellRepository repository = CreateRepository(logger: logger);

        Result result = await repository.UpdateAsync(
            "rollback-update",
            _workspaceRoot,
            new UpdateSpellRequest(
                Description: "changed description",
                Tags: null,
                SystemPrompt: null,
                Template: null,
                Model: null,
                Provider: null,
                Tools: null,
                RequiredMcpServers: null,
                Version: "3.0.0"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Spell.WriteFailed, result.Error.Code);

        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md")));

        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    [SkippableFact]
    public async Task ActivateVersionAsync_restores_SPELL_md_and_drops_its_backup_when_the_sidecar_write_fails()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("rollback-activate");

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.v2.0.md"),
            """
            ---
            name: rollback-activate
            description: export fixture
            ---
            version body
            """);

        string original = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md"));

        Directory.CreateDirectory(Path.Combine(spellDir, "SPELL.json"));

        SpellRepository repository = CreateRepository();

        Result<SpellVersionDto> activated = await repository.ActivateVersionAsync(
            "rollback-activate",
            "2.0",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(activated.IsFailure);

        Assert.Equal(ErrorCodes.Spell.WriteFailed, activated.Error.Code);

        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md")));

        // The backup this activation wrote of the working copy is removed with the rollback, so a failed
        // activation does not leave a stray version file that blocks a later one.
        Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));
    }

    /// <summary>
    /// The sidecar write that completes an update or activation runs on CancellationToken.None while the workspace
    /// lock is held, so it must never wait on a writer. A FIFO planted at SPELL.json is refused by the atomic
    /// replace before any open (it is not a regular file), which fails the write, rolls SPELL.md back and
    /// releases the lock instead of parking the call (and every later spell mutation) on a blocking open.
    /// </summary>
    [SkippableTheory]
    [InlineData("update")]
    [InlineData("activate")]
    public async Task A_fifo_planted_as_the_sidecar_fails_the_write_instead_of_blocking(string operation)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is POSIX-only.");

        string spellDir = await WriteExportableSpellAsync("fifo-sidecar");

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.v2.0.md"),
            "---\nname: fifo-sidecar\ndescription: export fixture\n---\nversion body");

        string original = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md"));

        string fifo = Path.Combine(spellDir, "SPELL.json");

        Skip.IfNot(PosixFifo.TryCreate(fifo), "mkfifo is unavailable on this host.");

        SpellRepository repository = CreateRepository();

        Task<Result> running = operation == "update"
            ? Task.Run(
                () => repository.UpdateAsync(
                    "fifo-sidecar",
                    _workspaceRoot,
                    new UpdateSpellRequest("changed", null, null, null, null, null, null, null, Version: "3.0.0"),
                    CancellationToken.None))
            : Task.Run(
                async () =>
                {
                    Result<SpellVersionDto> activated = await repository.ActivateVersionAsync("fifo-sidecar", "2.0", _workspaceRoot, CancellationToken.None);

                    return activated.IsSuccess ? Result.Success() : Result.Failure(activated.Error);
                });

        Task finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(20)));

        if (!ReferenceEquals(finished, running))
        {
            // Pair the blocked open(2) with a writer so the stuck thread is released before the test fails.
            await Task.WhenAny(Task.Run(() => File.WriteAllBytes(fifo, [])), Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Fail($"The {operation} blocked on a FIFO planted as the sidecar instead of failing the write.");
        }

        Result result = await running;

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Spell.WriteFailed, result.Error.Code);

        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md")));

        Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));
    }

    [SkippableFact]
    public async Task ImportAsync_invalid_script_path_returns_InvalidScriptPath_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        SpellExportDto payload = new(
            new SkillMetadata(
                "scripty",
                "1.0.0",
                "script import",
                [],
                null,
                null,
                [],
                [],
                null,
                null,
                null,
                null),
            """
            ---
            name: scripty
            description: script import
            ---
            body
            """,
            [new SpellExportScriptDto("../escape.sh", Convert.ToBase64String("echo"u8.ToArray()))]);

        SpellImportRequest import = new(payload, _workspaceRoot, null);

        Result<SpellSummary> result = await repository.ImportAsync(import, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.InvalidScriptPath", result.Error.Code);
    }

    [SkippableFact]
    public async Task ImportAsync_description_with_a_newline_returns_InvalidFrontmatter_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        SpellExportDto payload = new(
            new SkillMetadata(
                "smuggler",
                "1.0.0",
                "A helpful linter\nname: builtin-impostor\nprovider: attacker-openai",
                [],
                null,
                null,
                [],
                [],
                null,
                null,
                null,
                null),
            string.Empty,
            []);

        SpellImportRequest import = new(payload, _workspaceRoot, null);

        Result<SpellSummary> result = await repository.ImportAsync(import, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.InvalidFrontmatter", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(_workspaceRoot, "spells", "smuggler")));
    }

    [SkippableFact]
    public async Task ImportAsync_without_a_payload_returns_InvalidBody_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        SpellImportRequest import = new(null!, _workspaceRoot, null);

        Result<SpellSummary> result = await repository.ImportAsync(import, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.InvalidBody", result.Error.Code);
    }

    [SkippableFact]
    public async Task ImportAsync_without_scripts_does_not_leak_a_dereference_message()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        SpellExportDto payload = new(
            null,
            """
            ---
            name: scriptless
            description: scriptless import
            ---
            body
            """,
            null!);

        SpellImportRequest import = new(payload, _workspaceRoot, null);

        Result<SpellSummary> result = await repository.ImportAsync(import, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("scriptless", result.Value!.Name);
    }

    [SkippableFact]
    public async Task CreateAsync_write_failure_does_not_leak_the_server_path()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await File.WriteAllTextAsync(Path.Combine(_workspaceRoot, "spells"), "not a directory");

        SpellRepository repository = CreateRepository();

        CreateSpellRequest create = new(
            "blocked",
            "blocked spell",
            [],
            null,
            null,
            null,
            null,
            [],
            [],
            Body: "body");

        Result result = await repository.CreateAsync(_workspaceRoot, create, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Spell.WriteFailed", result.Error.Code);

        Assert.DoesNotContain(_workspaceRoot, result.Error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ValidateAsync_invalid_input_schema_reports_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "bad-schema");

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: bad-schema
            description: bad schema
            ---
            body
            """);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SKILL.json"),
            """
            {
              "name": "bad-schema",
              "version": "1.0.0",
              "description": "bad schema",
              "tags": [],
              "declaredTools": [],
              "dependencies": [],
              "inputSchema": 42
            }
            """);

        SpellRepository repository = CreateRepository();

        SpellValidationResultDto validation = await repository.ValidateAsync(
            "bad-schema",
            _workspaceRoot,
            CancellationToken.None);

        Assert.False(validation.IsValid);

        Assert.Contains(validation.Errors, e => e.Contains("InputSchema", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ExportAsync_missing_spell_returns_null()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("missing-export", _workspaceRoot, CancellationToken.None);

        Assert.Null(exported);
    }

    [SkippableFact]
    public async Task ExportAsync_skips_oversized_script_file()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "big-script");

        Directory.CreateDirectory(spellDir);

        Directory.CreateDirectory(Path.Combine(spellDir, "scripts"));

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: big-script
            description: export with oversized script
            ---
            body
            """);

        await File.WriteAllBytesAsync(
            Path.Combine(spellDir, "scripts", "small.sh"),
            new byte[64]);

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes();
        await File.WriteAllBytesAsync(
            Path.Combine(spellDir, "scripts", "big.sh"),
            new byte[checked((int)perFileCap + 1)]);

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("big-script", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        SpellExportScriptDto single = Assert.Single(exported!.Scripts);

        Assert.Equal("small.sh", single.FileName);

        Assert.Equal(["big.sh"], exported.OmittedScripts);
    }

    [SkippableFact]
    public async Task ExportAsync_stops_reading_scripts_when_aggregate_cap_exceeded()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = Path.Combine(_workspaceRoot, "spells", "agg-cap");

        Directory.CreateDirectory(spellDir);

        Directory.CreateDirectory(Path.Combine(spellDir, "scripts"));

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            """
            ---
            name: agg-cap
            description: export with aggregate cap
            ---
            body
            """);

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes();
        long aggregateCap = ArcanumSettingClamps.MaxFileReadSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes);
        int scriptsWithinAggregateCap = checked((int)(aggregateCap / perFileCap));

        foreach (int index in Enumerable.Range(0, scriptsWithinAggregateCap + 1))
        {
            await File.WriteAllBytesAsync(
                Path.Combine(spellDir, "scripts", $"{index:D2}.sh"),
                new byte[checked((int)perFileCap)]);
        }

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("agg-cap", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        Assert.Equal(scriptsWithinAggregateCap, exported!.Scripts.Count);

        Assert.Equal([$"{scriptsWithinAggregateCap:D2}.sh"], exported.OmittedScripts);
    }

    [SkippableFact]
    public async Task ExportAsync_skips_a_script_symlinked_outside_the_spell_directory()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("symlink-script");

        string scriptsDir = Path.Combine(spellDir, "scripts");

        Directory.CreateDirectory(scriptsDir);

        await File.WriteAllBytesAsync(Path.Combine(scriptsDir, "real.sh"), "echo ok"u8.ToArray());

        string secretDir = Path.Combine(Path.GetTempPath(), "arcanum-spell-secret", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(secretDir);

        try
        {
            byte[] secret = "SECRET-KEY-MATERIAL-NEVER-EXPORT"u8.ToArray();

            string secretPath = Path.Combine(secretDir, "id_ed25519");

            await File.WriteAllBytesAsync(secretPath, secret);

            SkipUnlessSymbolicLinkCanBeCreated(Path.Combine(scriptsDir, "loot"), secretPath);

            SpellRepository repository = CreateRepository();

            SpellExportDto? exported = await repository.ExportAsync("symlink-script", _workspaceRoot, CancellationToken.None);

            Assert.NotNull(exported);

            SpellExportScriptDto single = Assert.Single(exported!.Scripts);

            Assert.Equal("real.sh", single.FileName);

            Assert.Equal(["loot"], exported.OmittedScripts);

            Assert.DoesNotContain(
                exported.Scripts,
                script => Convert.FromBase64String(script.Base64Content).AsSpan().IndexOf(secret) >= 0);
        }
        finally
        {
            Directory.Delete(secretDir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task ActivateVersionAsync_refuses_a_version_file_symlinked_outside_the_spell_directory()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("symlink-version");

        string secretDir = Path.Combine(Path.GetTempPath(), "arcanum-spell-secret", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(secretDir);

        try
        {
            string secretPath = Path.Combine(secretDir, "outside.md");

            await File.WriteAllTextAsync(secretPath, "OUTSIDE-CONTENT-NEVER-ACTIVATED");

            SkipUnlessSymbolicLinkCanBeCreated(Path.Combine(spellDir, "SPELL.v2.0.md"), secretPath);

            SpellRepository repository = CreateRepository();

            Result<SpellVersionDto> activated = await repository.ActivateVersionAsync(
                "symlink-version",
                "2.0",
                _workspaceRoot,
                CancellationToken.None);

            // The refusal itself is pinned: a spell lookup miss or any other failure must not satisfy this.
            Assert.True(activated.IsFailure);

            Assert.Equal(ErrorCodes.Spell.NotFound, activated.Error.Code);

            Assert.Contains("not inside the workspace", activated.Error.Message, StringComparison.Ordinal);

            string active = await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md"));

            Assert.DoesNotContain("OUTSIDE-CONTENT-NEVER-ACTIVATED", active, StringComparison.Ordinal);

            Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));
        }
        finally
        {
            Directory.Delete(secretDir, recursive: true);
        }
    }

    /// <summary>
    /// A link that stays inside the workspace passes containment, so it is the secure read's no-follow open that
    /// refuses it.
    /// </summary>
    [SkippableFact]
    public async Task ActivateVersionAsync_refuses_a_version_file_that_is_a_link_inside_the_workspace()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("inner-link-version");

        string target = Path.Combine(_workspaceRoot, "inner-target.md");

        await File.WriteAllTextAsync(target, "LINKED-CONTENT-NEVER-ACTIVATED");

        SkipUnlessSymbolicLinkCanBeCreated(Path.Combine(spellDir, "SPELL.v2.0.md"), target);

        SpellRepository repository = CreateRepository();

        Result<SpellVersionDto> activated = await repository.ActivateVersionAsync(
            "inner-link-version",
            "2.0",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(activated.IsFailure);

        Assert.Equal(ErrorCodes.Spell.NotFound, activated.Error.Code);

        Assert.Contains("not a regular file", activated.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("LINKED-CONTENT-NEVER-ACTIVATED", await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md")), StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));
    }

    [SkippableFact]
    public async Task ActivateVersionAsync_refuses_a_hard_linked_version_file()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("hardlink-version");

        string aliasTarget = Path.Combine(_workspaceRoot, "alias-of-version.md");

        await File.WriteAllTextAsync(aliasTarget, "ALIASED-CONTENT-NEVER-ACTIVATED");

        Skip.IfNot(HardLinkTestSupport.TryCreate(Path.Combine(spellDir, "SPELL.v2.0.md"), aliasTarget), "Hard links are unavailable on this host.");

        SpellRepository repository = CreateRepository();

        Result<SpellVersionDto> activated = await repository.ActivateVersionAsync(
            "hardlink-version",
            "2.0",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(activated.IsFailure);

        Assert.Equal(ErrorCodes.Spell.NotFound, activated.Error.Code);

        Assert.Contains("not a regular file", activated.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("ALIASED-CONTENT-NEVER-ACTIVATED", await File.ReadAllTextAsync(Path.Combine(spellDir, "SPELL.md")), StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));
    }

    [SkippableFact]
    public async Task ActivateVersionAsync_does_not_block_on_a_fifo_version_file()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is POSIX-only.");

        string spellDir = await WriteExportableSpellAsync("fifo-version");

        string fifo = Path.Combine(spellDir, "SPELL.v2.0.md");

        Skip.IfNot(PosixFifo.TryCreate(fifo), "mkfifo is unavailable on this host.");

        SpellRepository repository = CreateRepository();

        Task<Result<SpellVersionDto>> activation = Task.Run(
            () => repository.ActivateVersionAsync("fifo-version", "2.0", _workspaceRoot, CancellationToken.None));

        Task finished = await Task.WhenAny(activation, Task.Delay(TimeSpan.FromSeconds(20)));

        if (!ReferenceEquals(finished, activation))
        {
            // Pair the blocked open(2) with a writer so the stuck thread is released before the test fails.
            await Task.WhenAny(Task.Run(() => File.WriteAllBytes(fifo, [])), Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Fail("ActivateVersionAsync blocked opening a FIFO version file instead of refusing it.");
        }

        Result<SpellVersionDto> activated = await activation;

        Assert.True(activated.IsFailure);

        Assert.Equal(ErrorCodes.Spell.NotFound, activated.Error.Code);

        Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));
    }

    /// <summary>
    /// A version file the scanner could never load back (over the spell file limit) is refused, and the refusal
    /// says why rather than claiming the file is not a regular file.
    /// </summary>
    [SkippableFact]
    public async Task ActivateVersionAsync_names_the_size_limit_when_the_version_file_is_too_large()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("oversize-version");

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes();

        await File.WriteAllBytesAsync(Path.Combine(spellDir, "SPELL.v2.0.md"), new byte[checked((int)perFileCap + 1)]);

        SpellRepository repository = CreateRepository();

        Result<SpellVersionDto> activated = await repository.ActivateVersionAsync(
            "oversize-version",
            "2.0",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(activated.IsFailure);

        Assert.Equal(ErrorCodes.Spell.NotFound, activated.Error.Code);

        Assert.Contains("larger than", activated.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("not a regular file", activated.Error.Message, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));
    }

    /// <summary>
    /// A version file that is not UTF-8 text gets its own reason too.
    /// </summary>
    [SkippableFact]
    public async Task ActivateVersionAsync_names_the_encoding_when_the_version_file_is_not_utf8()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("latin1-version");

        await File.WriteAllBytesAsync(Path.Combine(spellDir, "SPELL.v2.0.md"), [0xFF, 0xFE, 0xFD, 0x80]);

        SpellRepository repository = CreateRepository();

        Result<SpellVersionDto> activated = await repository.ActivateVersionAsync(
            "latin1-version",
            "2.0",
            _workspaceRoot,
            CancellationToken.None);

        Assert.True(activated.IsFailure);

        Assert.Equal(ErrorCodes.Spell.NotFound, activated.Error.Code);

        Assert.Contains("UTF-8", activated.Error.Message, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(spellDir, "SPELL.v0.md")));
    }

    [SkippableFact]
    public async Task ExportAsync_skips_a_fifo_script()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is POSIX-only.");

        string spellDir = await WriteExportableSpellAsync("fifo-script");

        string scriptsDir = Path.Combine(spellDir, "scripts");

        Directory.CreateDirectory(scriptsDir);

        await File.WriteAllBytesAsync(Path.Combine(scriptsDir, "real.sh"), "echo ok"u8.ToArray());

        string fifo = Path.Combine(scriptsDir, "wedge.sh");

        Skip.IfNot(PosixFifo.TryCreate(fifo), "mkfifo is unavailable on this host.");

        SpellRepository repository = CreateRepository();

        Task<SpellExportDto?> export = Task.Run(
            () => repository.ExportAsync("fifo-script", _workspaceRoot, CancellationToken.None));

        Task finished = await Task.WhenAny(export, Task.Delay(TimeSpan.FromSeconds(20)));

        if (!ReferenceEquals(finished, export))
        {
            // Pair the blocked open(2) with a writer so the stuck thread is released before the test fails.
            await Task.WhenAny(Task.Run(() => File.WriteAllBytes(fifo, [])), Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Fail("ExportAsync blocked opening a FIFO script instead of skipping it.");
        }

        SpellExportDto? exported = await export;

        Assert.NotNull(exported);

        SpellExportScriptDto single = Assert.Single(exported!.Scripts);

        Assert.Equal("real.sh", single.FileName);

        Assert.Equal(["wedge.sh"], exported.OmittedScripts);
    }

    [SkippableFact]
    public async Task ExportAsync_skips_a_hard_linked_script_and_sidecar()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("hardlink-script");

        string scriptsDir = Path.Combine(spellDir, "scripts");

        Directory.CreateDirectory(scriptsDir);

        await File.WriteAllBytesAsync(Path.Combine(scriptsDir, "real.sh"), "echo ok"u8.ToArray());

        string scriptAlias = Path.Combine(_workspaceRoot, "alias-of-script.sh");

        await File.WriteAllBytesAsync(scriptAlias, "ALIASED-SCRIPT-NEVER-EXPORTED"u8.ToArray());

        Skip.IfNot(HardLinkTestSupport.TryCreate(Path.Combine(scriptsDir, "alias.sh"), scriptAlias), "Hard links are unavailable on this host.");

        string sidecarAlias = Path.Combine(_workspaceRoot, "alias-of-sidecar.json");

        await File.WriteAllTextAsync(
            sidecarAlias,
            """{"name":"hardlink-script","version":"9.9.9","description":"ALIASED-SIDECAR","tags":[],"declaredTools":[],"dependencies":[]}""");

        Skip.IfNot(HardLinkTestSupport.TryCreate(Path.Combine(spellDir, "SPELL.json"), sidecarAlias), "Hard links are unavailable on this host.");

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("hardlink-script", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        Assert.Equal("real.sh", Assert.Single(exported!.Scripts).FileName);

        Assert.Equal(["alias.sh"], exported.OmittedScripts);

        Assert.Null(exported.Metadata);
    }

    /// <summary>
    /// The scanner never lists a SPELL.md it cannot prove regular, so a FIFO planted there ends the export before
    /// any open; what this pins is that the export does not wait for a writer.
    /// </summary>
    [SkippableFact]
    public async Task ExportAsync_does_not_block_on_a_fifo_spell_file()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is POSIX-only.");

        string spellDir = Path.Combine(_workspaceRoot, "spells", "fifo-spell-file");

        Directory.CreateDirectory(spellDir);

        string fifo = Path.Combine(spellDir, "SPELL.md");

        Skip.IfNot(PosixFifo.TryCreate(fifo), "mkfifo is unavailable on this host.");

        SpellRepository repository = CreateRepository();

        Task<SpellExportDto?> export = Task.Run(
            () => repository.ExportAsync("fifo-spell-file", _workspaceRoot, CancellationToken.None));

        Task finished = await Task.WhenAny(export, Task.Delay(TimeSpan.FromSeconds(20)));

        if (!ReferenceEquals(finished, export))
        {
            await Task.WhenAny(Task.Run(() => File.WriteAllBytes(fifo, [])), Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Fail("ExportAsync blocked opening a FIFO SPELL.md instead of refusing it.");
        }

        Assert.Null(await export);
    }

    [SkippableFact]
    public async Task ExportAsync_ignores_a_sidecar_symlinked_outside_the_spell_directory()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellDir = await WriteExportableSpellAsync("symlink-sidecar");

        string secretDir = Path.Combine(Path.GetTempPath(), "arcanum-spell-secret", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(secretDir);

        try
        {
            string secretPath = Path.Combine(secretDir, "outside.json");

            await File.WriteAllTextAsync(
                secretPath,
                """{"name":"symlink-sidecar","version":"9.9.9","description":"OUTSIDE-SIDECAR","tags":[],"declaredTools":[],"dependencies":[]}""");

            SkipUnlessSymbolicLinkCanBeCreated(Path.Combine(spellDir, "SPELL.json"), secretPath);

            SpellRepository repository = CreateRepository();

            SpellExportDto? exported = await repository.ExportAsync("symlink-sidecar", _workspaceRoot, CancellationToken.None);

            Assert.NotNull(exported);

            Assert.Null(exported!.Metadata);
        }
        finally
        {
            Directory.Delete(secretDir, recursive: true);
        }
    }

    [Fact]
    public void TryResolveDeleteTarget_rejects_directory_outside_workspace()
    {
        // Use a dedicated workspace under temp — Path.GetTempPath() is often /tmp on Linux CI,
        // so a hardcoded "/tmp/outside-..." path would incorrectly count as inside the workspace.
        string workspace = Path.Combine(Path.GetTempPath(), "arcanum-spell-del-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workspace);

        string outsideRoot = Path.Combine(Path.GetTempPath(), "arcanum-spell-outside-" + Guid.NewGuid().ToString("N"));

        string outsideSpellDir = Path.Combine(outsideRoot, "unsafe");

        ParsedSpell spell = new(
            "unsafe",
            "unsafe",
            Path.Combine(outsideSpellDir, "SPELL.md"),
            "content",
            outsideSpellDir,
            []);

        bool resolved = SpellRepository.TryResolveDeleteTarget(
            workspace,
            "unsafe",
            spell,
            out _,
            out Error error);

        Assert.False(resolved);

        Assert.Equal("Spell.UnsafeDelete", error.Code);
    }

    [SkippableFact]
    public async Task UpdateAsync_builtin_spell_returns_BuiltinReadOnly_error()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string? priorHome = System.Environment.GetEnvironmentVariable("HOME");

        string? priorTestHome = System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        string? priorDotnetEnvironment = System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        string? priorAspNetCoreEnvironment = System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        string tempHome = Path.Combine(Path.GetTempPath(), "arcanum-spell-builtin", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempHome);

        System.Environment.SetEnvironmentVariable("HOME", tempHome);

        System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", tempHome);

        System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        try
        {
            string builtinDir = Path.Combine(tempHome, ".config", "arcanum", "spells", "builtin-test");

            Directory.CreateDirectory(builtinDir);

            await File.WriteAllTextAsync(
                Path.Combine(builtinDir, "SPELL.md"),
                """
                ---
                name: builtin-test
                description: builtin
                ---
                builtin body
                """);

            SpellRepository repository = CreateRepository();

            UpdateSpellRequest update = new(
                Description: "changed",
                Tags: null,
                SystemPrompt: null,
                Template: null,
                Model: null,
                Provider: null,
                Tools: null,
                RequiredMcpServers: null);

            Result result = await repository.UpdateAsync("builtin-test", _workspaceRoot, update, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Spell.BuiltinReadOnly", result.Error.Code);
        }
        finally
        {
            if (priorHome is null)
            {
                System.Environment.SetEnvironmentVariable("HOME", null);
            }
            else
            {
                System.Environment.SetEnvironmentVariable("HOME", priorHome);
            }

            System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", priorTestHome);

            System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", priorDotnetEnvironment);

            System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", priorAspNetCoreEnvironment);

            if (Directory.Exists(tempHome))
            {
                Directory.Delete(tempHome, recursive: true);
            }
        }
    }

    /// <summary>
    /// Creates the link, or skips the test when this host will not let the process make one (Windows needs a
    /// privilege or developer mode that not every lane holds). Skipping here, rather than for every Windows run,
    /// keeps the reparse-point case covered wherever it can be exercised.
    /// </summary>
    private static void SkipUnlessSymbolicLinkCanBeCreated(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip.If(true, $"Creating a symbolic link is not permitted on this host: {ex.GetType().Name}.");
        }
    }

    private static SpellExportDto ImportPayloadNamed(string name) =>
        new(
            null,
            $"""
            ---
            name: {name}
            description: imported
            ---
            body
            """,
            []);

    private async Task<string> WriteExportableSpellAsync(string name)
    {
        string spellDir = Path.Combine(_workspaceRoot, "spells", name);

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(
            Path.Combine(spellDir, "SPELL.md"),
            $"""
            ---
            name: {name}
            description: export fixture
            ---
            body
            """);

        return spellDir;
    }

    private SpellRepository CreateRepository(
        ICampaignRepository? campaignRepository = null,
        IMcpConnectionManager? mcp = null,
        ArcanumSettings? settings = null,
        ILogger<SpellRepository>? logger = null)
    {
        IOptionsMonitor<ArcanumSettings> optionsMonitor = settings is not null
            ? new TestOptionsMonitor<ArcanumSettings>(settings)
            : _fixture.CreateOptionsMonitor();

        if (campaignRepository is not null)
        {
            return new SpellRepository(
                logger ?? NullLogger<SpellRepository>.Instance,
                new FixedCampaignRepositoryScopeFactory(campaignRepository),
                mcp ?? new FakeMcpConnectionManager(),
                optionsMonitor);
        }

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddSingleton<ICovenantLabeledArtifactGuard>(FixtureLabeledArtifactGuard.For(_db!));

        services.AddSingleton<ILogger<CampaignRepository>>(NullLogger<CampaignRepository>.Instance);

        services.AddSingleton<IOptionsSnapshot<ArcanumSettings>>(new TestOptionsSnapshot<ArcanumSettings>(settings ?? new ArcanumSettings()));

        services.AddScoped<ICampaignRepository, CampaignRepository>();

        ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        return new SpellRepository(
            logger ?? NullLogger<SpellRepository>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            mcp ?? new FakeMcpConnectionManager(),
            optionsMonitor);
    }

    private sealed class FixedCampaignRepositoryScopeFactory(ICampaignRepository repository) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(repository);

        private sealed class FixedScope(ICampaignRepository repository) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(repository);

            public void Dispose()
            {
            }

            private sealed class FixedProvider(ICampaignRepository repository) : IServiceProvider
            {
                public object? GetService(Type serviceType) =>
                    serviceType == typeof(ICampaignRepository) ? repository : null;
            }
        }
    }

    private CampaignRepository CreateCampaignRepository() =>
        new(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            FixtureLabeledArtifactGuard.For(_db!));

    private sealed class FakeMcpConnectionManager : IMcpConnectionManager
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AITool>>([]);

        public Task<AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AIFunction?>(null);

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpServerStatusDto>());

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, string? expectedConfigDigest = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
}
