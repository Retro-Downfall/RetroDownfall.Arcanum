using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspace;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

public sealed class SkillJsonIOTests
{

    [Fact]
    public void HasStructuredFields_detects_create_request_fields()
    {

        CreateSpellRequest empty = new(
            "n", null, [], null, null, null, null, [], []);

        Assert.False(SkillJsonIO.HasStructuredFields(empty));

        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\"}");

        CreateSpellRequest structured = empty with
        {
            Version = "2.0.0",
            InputSchema = schema,
        };

        Assert.True(SkillJsonIO.HasStructuredFields(structured));

    }

    [Fact]
    public void BuildMetadataFromCreate_applies_defaults()
    {

        CreateSpellRequest request = new(
            "n",
            "desc",
            ["tag"],
            null,
            null,
            "model",
            "provider",
            [],
            []);

        SkillMetadata metadata = SkillJsonIO.BuildMetadataFromCreate("spell-a", request);

        Assert.Equal("spell-a", metadata.Name);

        Assert.Equal("1.0.0", metadata.Version);

        Assert.Equal("desc", metadata.Description);

        Assert.Equal(["tag"], metadata.Tags);

    }

    [Fact]
    public void MergeMetadata_prefers_update_values_and_existing_fallbacks()
    {

        SkillMetadata existingMeta = new(
            "old",
            "1.0.0",
            "old-desc",
            ["t"],
            null,
            null,
            [],
            [],
            "old-model",
            "old-provider",
            null,
            DateTimeOffset.UtcNow);

        ParsedSpell existing = new(
            "spell",
            "old-desc",
            "/tmp/spell/SKILL.md",
            "content",
            "/tmp/spell",
            [])
        {
            Tags = ["t"],
            Model = "old-model",
            Provider = "old-provider",
            SkillMetadata = existingMeta,
        };

        UpdateSpellRequest update = new(
            Description: "new-desc",
            Tags: null,
            SystemPrompt: null,
            Template: null,
            Model: "new-model",
            Provider: null,
            Tools: null,
            RequiredMcpServers: null,
            Version: "3.0.0");

        SkillMetadata merged = SkillJsonIO.MergeMetadata(existing, update);

        Assert.Equal("3.0.0", merged.Version);

        Assert.Equal("new-desc", merged.Description);

        Assert.Equal("new-model", merged.Model);

        Assert.Equal("old-provider", merged.Provider);

        Assert.Equal(["t"], merged.Tags);

    }

    [Fact]
    public void HasStructuredFields_detects_update_request_fields()
    {

        UpdateSpellRequest empty = new(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        Assert.False(SkillJsonIO.HasStructuredFields(empty));

        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\"}");

        UpdateSpellRequest structured = empty with
        {
            OutputSchema = schema,
            DeclaredTools = [],
        };

        Assert.True(SkillJsonIO.HasStructuredFields(structured));

    }

    [Fact]
    public void MergeMetadata_without_existing_skill_metadata_uses_spell_fields()
    {

        ParsedSpell existing = new(
            "spell",
            "desc",
            "/tmp/spell/SKILL.md",
            "content",
            "/tmp/spell",
            ["tool-a"])
        {
            Tags = ["tag-a"],
            Model = "model-a",
            Provider = "provider-a",
        };

        UpdateSpellRequest update = new(
            Description: null,
            Tags: ["tag-b"],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: "provider-b",
            Tools: null,
            RequiredMcpServers: null,
            Version: null);

        SkillMetadata merged = SkillJsonIO.MergeMetadata(existing, update);

        Assert.Equal("desc", merged.Description);

        Assert.Equal(["tag-b"], merged.Tags);

        Assert.Equal("model-a", merged.Model);

        Assert.Equal("provider-b", merged.Provider);

    }

    [Fact]
    public async Task WriteAsync_writes_SKILL_json_file()
    {

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-skill-json", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        try
        {

            SkillMetadata metadata = new(
                "file-spell",
                "1.0.0",
                null,
                [],
                null,
                null,
                [],
                [],
                null,
                null,
                null,
                DateTimeOffset.UtcNow);

            await SkillJsonIO.WriteAsync(dir, metadata, CancellationToken.None);

            string path = Path.Combine(dir, "SKILL.json");

            Assert.True(File.Exists(path));

            string json = await File.ReadAllTextAsync(path);

            Assert.Contains("file-spell", json, StringComparison.Ordinal);

        }
        finally
        {

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

        }

    }

}
