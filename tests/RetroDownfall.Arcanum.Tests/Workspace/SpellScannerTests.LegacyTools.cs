using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed partial class SpellScannerTests
{
    [Fact]
    public async Task Parsing_retains_legacy_declarations_with_static_diagnostics_without_guessing_external_availability()
    {
        const string sidecar = """{"name":"legacy-tools","version":"1","tags":[],"declaredTools":["READ_FILE_CHUNK","custom_external_tool"],"dependencies":[]}""";

        _workspace.WriteFile("spells/legacy-tools/SPELL.md", "---\nname: legacy-tools\n---\nBody");

        _workspace.WriteFile("spells/legacy-tools/SPELL.json", sidecar);

        var spells = await SpellScanner.ScanAsync(_workspace.Root, default, MaxFileSizeBytes);

        ParsedSpell spell = Assert.Single(spells, item => item.Name == "legacy-tools");

        Assert.Equal(["READ_FILE_CHUNK", "custom_external_tool"], spell.SkillMetadata!.DeclaredTools);

        ToolNameDiagnostic diagnostic = Assert.Single(spell.ToolDiagnostics);

        Assert.Equal(ToolNameDiagnosticKind.Removed, diagnostic.Kind);

        Assert.Equal("READ_FILE_CHUNK", diagnostic.ToolName);

        Assert.Equal("read_file", diagnostic.Replacement);

        Assert.Equal(sidecar, await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "spells/legacy-tools/SPELL.json")));
    }
}
