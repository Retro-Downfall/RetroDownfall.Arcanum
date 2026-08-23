using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class SpellJsonSyncTests
{

    [Fact]
    public void LoadFromSpell_MapsKnownFields_AndUsesEmptyObjectForNullSchemas()
    {

        using JsonDocument input = JsonDocument.Parse("""{"type":"object"}""");

        SpellDetail spell = new(
            "mend-armor",
            "Repairs armor",
            SpellSource.Workspace,
            ["repair"],
            "system",
            null,
            "body",
            "gpt-4o",
            "openai",
            ["attuned-tool"],
            [],
            "/tmp",
            "/tmp/SPELL.md",
            Version: "1.2.0",
            InputSchema: input,
            OutputSchema: null,
            DeclaredTools: ["tool-a"],
            Dependencies: ["other-spell"],
            ActiveVersion: "v3");

        SpellJsonSync.DesignerState state = SpellJsonSync.LoadFromSpell(spell);

        Assert.Equal("1.2.0", state.Version);

        Assert.Equal("v3", state.ActiveVersion);

        Assert.Equal(["other-spell"], state.Dependencies);

        Assert.Equal(["tool-a"], state.DeclaredTools);

        Assert.Contains("\"type\"", state.InputSchemaJson);

        Assert.Equal("{}", state.OutputSchemaJson);

    }

    [Fact]
    public void LoadFromSpell_WhenNull_ReturnsEmptyDefaults()
    {

        SpellJsonSync.DesignerState state = SpellJsonSync.LoadFromSpell(null);

        Assert.Equal(string.Empty, state.Version);

        Assert.Null(state.ActiveVersion);

        Assert.Empty(state.Dependencies);

        Assert.Empty(state.DeclaredTools);

        Assert.Equal("{}", state.InputSchemaJson);

        Assert.Equal("{}", state.OutputSchemaJson);

    }

    [Fact]
    public void SerializeKnownFields_RoundTripsKnownMetadata()
    {

        using JsonDocument input = JsonDocument.Parse("""{"type":"object"}""");

        SpellDetail spell = new(
            "mend-armor",
            "Repairs armor",
            SpellSource.Workspace,
            ["repair", "armor"],
            "system",
            null,
            "body",
            "gpt-4o",
            "openai",
            ["attuned-tool"],
            [],
            "/tmp",
            "/tmp/SPELL.md",
            Version: "1.0.0",
            InputSchema: input,
            OutputSchema: null,
            DeclaredTools: ["tool-a"],
            Dependencies: ["dep-a"],
            ActiveVersion: "v1");

        string json = SpellJsonSync.SerializeKnownFields(spell);

        Assert.DoesNotContain("attuned-tool", json);

        Assert.Contains("tool-a", json);

        Assert.True(SpellJsonSync.TryParseRaw(json, out SkillMetadata? metadata, out string? error), error ?? "parse failed");

        Assert.Null(error);

        Assert.NotNull(metadata);

        Assert.Equal("mend-armor", metadata.Name);

        Assert.Equal("1.0.0", metadata.Version);

        Assert.Equal("Repairs armor", metadata.Description);

        Assert.Equal(["repair", "armor"], metadata.Tags);

        Assert.Equal(["tool-a"], metadata.DeclaredTools);

        Assert.Equal(["dep-a"], metadata.Dependencies);

        Assert.Equal("v1", metadata.ActiveVersion);

        Assert.Equal("gpt-4o", metadata.Model);

        Assert.Equal("openai", metadata.Provider);

        Assert.NotNull(metadata.InputSchema);

        Assert.Contains("\"type\"", metadata.InputSchema.RootElement.GetRawText());

        // Source-gen may materialize omitted schemas as a JsonDocument whose root is null.
        Assert.True(
            metadata.OutputSchema is null
            || metadata.OutputSchema.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || (metadata.OutputSchema.RootElement.ValueKind == JsonValueKind.Object
                && !metadata.OutputSchema.RootElement.EnumerateObject().Any()));

    }

    [Fact]
    public void SerializeKnownFields_FromDesignerState_OverridesMutableFields()
    {

        SpellDetail spell = new(
            "mend-armor",
            "Repairs armor",
            SpellSource.Workspace,
            ["repair"],
            "system",
            null,
            "body",
            "gpt-4o",
            "openai",
            [],
            [],
            "/tmp",
            "/tmp/SPELL.md",
            Version: "1.0.0",
            DeclaredTools: ["old-tool"],
            Dependencies: ["old-dep"],
            ActiveVersion: "v1");

        SpellJsonSync.DesignerState designer = new(
            Version: "2.0.0",
            ActiveVersion: "v1",
            Dependencies: ["new-dep"],
            DeclaredTools: ["new-tool"],
            InputSchemaJson: """{"type":"string"}""",
            OutputSchemaJson: "{}");

        string json = SpellJsonSync.SerializeKnownFields(spell, designer);

        Assert.True(SpellJsonSync.TryParseRaw(json, out SkillMetadata? metadata, out _));

        Assert.NotNull(metadata);

        Assert.Equal("2.0.0", metadata.Version);

        Assert.Equal(["new-dep"], metadata.Dependencies);

        Assert.Equal(["new-tool"], metadata.DeclaredTools);

        Assert.NotNull(metadata.InputSchema);

    }

    [Fact]
    public void TryParseRaw_InvalidJson_ReturnsError()
    {

        Assert.False(SpellJsonSync.TryParseRaw("{not-json", out SkillMetadata? metadata, out string? error));

        Assert.Null(metadata);

        Assert.False(string.IsNullOrWhiteSpace(error));

    }

    [Fact]
    public void TryParseSchemaJson_AcceptsEmptyAndObject()
    {

        Assert.True(SpellJsonSync.TryParseSchemaJson("", out JsonDocument? emptyDoc, out string? emptyError));

        Assert.Null(emptyError);

        Assert.NotNull(emptyDoc);

        Assert.Equal(JsonValueKind.Object, emptyDoc.RootElement.ValueKind);

        emptyDoc.Dispose();

        Assert.True(SpellJsonSync.TryParseSchemaJson("{}", out JsonDocument? objectDoc, out string? objectError));

        Assert.Null(objectError);

        Assert.NotNull(objectDoc);

        objectDoc.Dispose();

    }

    [Fact]
    public void TryParseSchemaJson_Invalid_ReturnsError()
    {

        Assert.False(SpellJsonSync.TryParseSchemaJson("[1,2", out JsonDocument? document, out string? error));

        Assert.Null(document);

        Assert.False(string.IsNullOrWhiteSpace(error));

    }

    [Fact]
    public void TryBuildUpdateFields_ParsesSchemasAndArrays()
    {

        SpellJsonSync.DesignerState designer = new(
            Version: "3.1.0",
            ActiveVersion: "v9",
            Dependencies: ["dep-b"],
            DeclaredTools: ["tool-b"],
            InputSchemaJson: """{"type":"number"}""",
            OutputSchemaJson: "{}");

        Assert.True(SpellJsonSync.TryBuildUpdateFields(
            designer,
            out string? version,
            out JsonDocument? inputSchema,
            out JsonDocument? outputSchema,
            out string[]? declaredTools,
            out string[]? dependencies,
            out string? error));

        Assert.Null(error);

        Assert.Equal("3.1.0", version);

        Assert.NotNull(inputSchema);

        Assert.NotNull(outputSchema);

        Assert.NotNull(declaredTools);

        Assert.NotNull(dependencies);

        Assert.Equal(["tool-b"], declaredTools);

        Assert.Equal(["dep-b"], dependencies);

        inputSchema.Dispose();

        outputSchema.Dispose();

    }

    [Fact]
    public void TryBuildUpdateFields_InvalidSchema_Fails()
    {

        SpellJsonSync.DesignerState designer = new(
            Version: "1.0.0",
            ActiveVersion: null,
            Dependencies: [],
            DeclaredTools: [],
            InputSchemaJson: "{bad",
            OutputSchemaJson: "{}");

        Assert.False(SpellJsonSync.TryBuildUpdateFields(
            designer,
            out _,
            out JsonDocument? inputSchema,
            out JsonDocument? outputSchema,
            out _,
            out _,
            out string? error));

        Assert.Null(inputSchema);

        Assert.Null(outputSchema);

        Assert.False(string.IsNullOrWhiteSpace(error));

    }

}
