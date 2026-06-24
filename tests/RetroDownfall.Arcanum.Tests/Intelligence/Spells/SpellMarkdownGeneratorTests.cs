using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

public sealed class SpellMarkdownGeneratorTests
{

    [Fact]
    public void Generate_includes_frontmatter_and_heading()
    {

        SkillMetadata metadata = new(
            "summon",
            "1.0.0",
            "Calls allies",
            ["utility"],
            null,
            null,
            [],
            [],
            "gpt",
            "local",
            null,
            DateTimeOffset.UtcNow);

        string markdown = SpellMarkdownGenerator.Generate(metadata);

        Assert.Contains("name: summon", markdown, StringComparison.Ordinal);

        Assert.Contains("description: Calls allies", markdown, StringComparison.Ordinal);

        Assert.Contains("tags: [utility]", markdown, StringComparison.Ordinal);

        Assert.Contains("# summon", markdown, StringComparison.Ordinal);

    }

    [Fact]
    public void GenerateFromCreateRequest_uses_request_defaults()
    {

        CreateSpellRequest request = new(
            Name: "ignored",
            Description: "from request",
            Tags: ["a"],
            SystemPrompt: null,
            Template: null,
            Model: "m1",
            Provider: "p1",
            Tools: [],
            RequiredMcpServers: [],
            Version: "2.0.0");

        string markdown = SpellMarkdownGenerator.GenerateFromCreateRequest("created", request);

        Assert.Contains("name: created", markdown, StringComparison.Ordinal);

        Assert.Contains("model: m1", markdown, StringComparison.Ordinal);

        Assert.Contains("provider: p1", markdown, StringComparison.Ordinal);

    }

}
