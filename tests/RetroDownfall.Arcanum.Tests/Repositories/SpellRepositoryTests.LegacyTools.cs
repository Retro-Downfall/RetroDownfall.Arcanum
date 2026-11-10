using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed partial class SpellRepositoryTests
{
    [SkippableTheory]
    [InlineData("read_file_chunk", "read_file")]
    [InlineData("DISPATCH_SENDING", "create_agent_task")]
    [InlineData("run_spell_script", "run_script")]
    public async Task ValidateAsync_rejects_removed_tool_with_replacement_without_rewriting_authored_content(string removed, string replacement)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        Assert.True((await repository.CreateAsync(_workspaceRoot,
            new CreateSpellRequest("old-tool", "Old tool", [], null, null, null, null, [], [],
                Body: "Body", InputSchema: System.Text.Json.JsonDocument.Parse("{}"),
                OutputSchema: System.Text.Json.JsonDocument.Parse("{}"), DeclaredTools: [removed]), default)).IsSuccess);

        SpellDetail before = (await repository.GetAsync("old-tool", _workspaceRoot, default))!;

        string sidecar = Path.Combine(Path.GetDirectoryName(before.FilePath)!, "SPELL.json");

        string bytes = await File.ReadAllTextAsync(sidecar);

        SpellValidationResultDto validation = await repository.ValidateAsync("old-tool", _workspaceRoot, default);

        Assert.False(validation.IsValid);

        Assert.Contains(validation.Errors, error => error.Contains(removed, StringComparison.Ordinal)
            && error.Contains(replacement, StringComparison.Ordinal));

        Assert.Equal(bytes, await File.ReadAllTextAsync(sidecar));

        Assert.Equal([removed], (await repository.GetAsync("old-tool", _workspaceRoot, default))!.DeclaredTools!);
    }

    [SkippableTheory]
    [InlineData("web_search")]
    [InlineData("read_url")]
    [InlineData("browse_web")]
    [InlineData("run_script")]
    public async Task ValidateAsync_recognizes_available_native_tools_outside_mcp_discovery(string nativeTool)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HostProcessToolsEscapeHatchScope? processTools = nativeTool == "run_script" ? new() : null;

        SpellRepository repository = CreateRepository(settings: new ArcanumSettings
        {
            Edition = ArcanumEdition.Development,
            Features = new() { WebBrowsing = true },
        });

        Assert.True((await repository.CreateAsync(_workspaceRoot,
            new CreateSpellRequest("native-tools", "Tools", [], null, null, null, null, [], [],
                Body: "Body", DeclaredTools: [nativeTool]), default)).IsSuccess);

        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "spells", "native-tools", "scripts"));

        await File.WriteAllTextAsync(Path.Combine(_workspaceRoot, "spells", "native-tools", "scripts", "test.sh"), "echo ok");

        SpellValidationResultDto validation = await repository.ValidateAsync("native-tools", _workspaceRoot, default);

        Assert.DoesNotContain(validation.Warnings, warning => warning.Contains(nativeTool, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ValidateAsync_uses_the_requested_workspace_surface_for_external_declarations()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository(mcp: new FakeMcpConnectionManager(_workspaceRoot));

        Assert.True((await repository.CreateAsync(_workspaceRoot,
            new CreateSpellRequest("workspace-tools", "Tools", [], null, null, null, null, [], [],
                Body: "Body", DeclaredTools: ["workspace_tool"]), default)).IsSuccess);

        SpellValidationResultDto validation = await repository.ValidateAsync("workspace-tools", _workspaceRoot, default);

        Assert.DoesNotContain(validation.Warnings, warning => warning.Contains("workspace_tool", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ValidateAsync_distinguishes_known_unavailable_from_unknown_tool()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SpellRepository repository = CreateRepository();

        Assert.True((await repository.CreateAsync(_workspaceRoot,
            new CreateSpellRequest("missing-tools", "Tools", [], null, null, null, null, [], [],
                Body: "Body", InputSchema: System.Text.Json.JsonDocument.Parse("{}"),
                OutputSchema: System.Text.Json.JsonDocument.Parse("{}"), DeclaredTools: ["create_agent_task", "made_up_external_tool"]), default)).IsSuccess);

        SpellValidationResultDto validation = await repository.ValidateAsync("missing-tools", _workspaceRoot, default);

        Assert.Contains(validation.Warnings, warning => warning.Contains("create_agent_task", StringComparison.Ordinal)
            && warning.Contains("unavailable", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(validation.Warnings, warning => warning.Contains("made_up_external_tool", StringComparison.Ordinal)
            && warning.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }
}
