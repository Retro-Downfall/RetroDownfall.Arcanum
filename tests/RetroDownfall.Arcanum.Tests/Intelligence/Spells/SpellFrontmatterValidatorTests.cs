using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Spells;

public sealed class SpellFrontmatterValidatorTests
{

    [Fact]
    public void ValidateCreate_allows_null_optional_fields()
    {

        CreateSpellRequest request = new(
            Name: "x",
            Description: null,
            Tags: [],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: [],
            RequiredMcpServers: []);

        Assert.Null(SpellFrontmatterValidator.ValidateCreate(request));

    }

    [Fact]
    public void ValidateCreate_rejects_line_breaks_in_scalar_fields()
    {

        CreateSpellRequest request = new(
            Name: "x",
            Description: "bad\nline",
            Tags: [],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: [],
            RequiredMcpServers: []);

        string? error = SpellFrontmatterValidator.ValidateCreate(request);

        Assert.NotNull(error);

        Assert.Contains("description", error!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void ValidateCreate_rejects_frontmatter_delimiter_in_values()
    {

        CreateSpellRequest request = new(
            Name: "x",
            Description: "---",
            Tags: [],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: [],
            RequiredMcpServers: []);

        string? error = SpellFrontmatterValidator.ValidateCreate(request);

        Assert.NotNull(error);

        Assert.Contains("---", error!, StringComparison.Ordinal);

    }

    [Fact]
    public void ValidateUpdate_validates_array_entries()
    {

        UpdateSpellRequest request = new(
            Description: null,
            Tags: ["ok", "bad\n"],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: null,
            RequiredMcpServers: null);

        string? error = SpellFrontmatterValidator.ValidateUpdate(request);

        Assert.NotNull(error);

        Assert.Contains("tags", error!, StringComparison.OrdinalIgnoreCase);

    }

}
