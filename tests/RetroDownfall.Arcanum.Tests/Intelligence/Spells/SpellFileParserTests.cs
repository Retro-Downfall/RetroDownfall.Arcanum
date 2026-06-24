using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

public sealed class SpellFileParserTests
{

    [Fact]
    public void Parse_without_frontmatter_returns_body_and_fallback_name()
    {

        string text = "# Title\n\nBody only.";

        SpellParseResult result = SpellFileParser.Parse(text, "fallback");

        Assert.Equal("fallback", result.Name);

        Assert.Equal(text, result.Body);

        Assert.Empty(result.Tags);

    }

    [Fact]
    public void Parse_reads_yaml_frontmatter_fields()
    {

        string text = """
            ---
            name: fireball
            description: A blazing spell
            tags: [combat, fire]
            model: gpt-test
            provider: local
            tools: read_file, write_file
            requiredMcpServers: [filesystem]
            ---
            # Fireball

            Cast it.
            """;

        SpellParseResult result = SpellFileParser.Parse(text, "fallback");

        Assert.Equal("fireball", result.Name);

        Assert.Equal("A blazing spell", result.Description);

        Assert.Equal(["combat", "fire"], result.Tags);

        Assert.Equal("gpt-test", result.Model);

        Assert.Equal("local", result.Provider);

        Assert.Equal(["read_file", "write_file"], result.Tools);

        Assert.Equal(["filesystem"], result.RequiredMcpServers);

        Assert.Contains("# Fireball", result.Body, StringComparison.Ordinal);

    }

    [Fact]
    public void FormatCreate_round_trips_key_fields()
    {

        CreateSpellRequest request = new(
            Name: "ignored",
            Description: "desc",
            Tags: ["a"],
            SystemPrompt: "sys",
            Template: "tmpl",
            Model: "m",
            Provider: "p",
            Tools: ["t1"],
            RequiredMcpServers: ["mcp"],
            Body: "body");

        string formatted = SpellFileParser.FormatCreate("my-spell", request);

        SpellParseResult parsed = SpellFileParser.Parse(formatted, "fallback");

        Assert.Equal("my-spell", parsed.Name);

        Assert.Equal("desc", parsed.Description);

        Assert.Equal(["a"], parsed.Tags);

        Assert.Equal("sys", parsed.SystemPrompt);

        Assert.Equal("body", parsed.Body.Trim());

    }

}
