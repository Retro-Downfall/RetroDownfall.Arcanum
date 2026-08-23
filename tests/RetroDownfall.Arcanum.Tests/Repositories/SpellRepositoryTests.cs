using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
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

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(
            new ArcanumSettings());
        await File.WriteAllBytesAsync(
            Path.Combine(spellDir, "scripts", "big.sh"),
            new byte[checked((int)perFileCap + 1)]);

        SpellRepository repository = CreateRepository();

        SpellExportDto? exported = await repository.ExportAsync("big-script", _workspaceRoot, CancellationToken.None);

        Assert.NotNull(exported);

        SpellExportScriptDto single = Assert.Single(exported!.Scripts);

        Assert.Equal("small.sh", single.FileName);

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

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(
            new ArcanumSettings());
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

    private SpellRepository CreateRepository(
        ICampaignRepository? campaignRepository = null,
        IMcpConnectionManager? mcp = null,
        ArcanumSettings? settings = null)
    {

        IOptionsMonitor<ArcanumSettings> optionsMonitor = settings is not null
            ? new TestOptionsMonitor<ArcanumSettings>(settings)
            : _fixture.CreateOptionsMonitor();

        if (campaignRepository is not null)
        {

            return new SpellRepository(
                NullLogger<SpellRepository>.Instance,
                new FixedCampaignRepositoryScopeFactory(campaignRepository),
                mcp ?? new FakeMcpConnectionManager(),
                optionsMonitor);

        }

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddSingleton<ILogger<CampaignRepository>>(NullLogger<CampaignRepository>.Instance);

        services.AddSingleton<IOptionsSnapshot<ArcanumSettings>>(new TestOptionsSnapshot<ArcanumSettings>(settings ?? new ArcanumSettings()));

        services.AddScoped<ICampaignRepository, CampaignRepository>();

        ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        return new SpellRepository(
            NullLogger<SpellRepository>.Instance,
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
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

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

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

    }

}
