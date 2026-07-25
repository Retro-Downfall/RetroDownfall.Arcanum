using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class SpellVersionPathPolicyTests
{

    [Theory]
    [InlineData("1")]
    [InlineData("1.2.3")]
    [InlineData("Alpha")]
    [InlineData("RC1.beta.2")]
    [InlineData("...")]
    public void IsValidLabel_accepts_documented_alphanumeric_and_dot_labels(string label)
    {

        Assert.True(SpellVersionPathPolicy.IsValidLabel(label));

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1-2")]
    [InlineData("v1_2")]
    [InlineData("../1")]
    [InlineData("1/2")]
    [InlineData("1\r\n2")]
    public void IsValidLabel_rejects_blank_or_non_policy_characters(string? label)
    {

        Assert.False(SpellVersionPathPolicy.IsValidLabel(label));

    }

    [Fact]
    public void BuildVersionFileName_preserves_label_exactly()
    {

        Assert.Equal(
            "SPELL.vRC1.beta.2.md",
            SpellVersionPathPolicy.BuildVersionFileName("RC1.beta.2"));

    }

    [Theory]
    [InlineData("SPELL.v1.md", "1")]
    [InlineData("spell.vAlpha.2.MD", "Alpha.2")]
    [InlineData("SpElL.vRC1.beta.md", "RC1.beta")]
    public void TryParseLabelFromFileName_accepts_exact_case_insensitive_sidecar_shape(
        string fileName,
        string expectedLabel)
    {

        bool parsed = SpellVersionPathPolicy.TryParseLabelFromFileName(fileName, out string label);

        Assert.True(parsed);

        Assert.Equal(expectedLabel, label);

    }

    [Theory]
    [InlineData("SPELL.md")]
    [InlineData("SPELL.v.md")]
    [InlineData("SPELL.v1-2.md")]
    [InlineData("prefix-SPELL.v1.md")]
    [InlineData("SPELL.v1.md.bak")]
    [InlineData("SPELL.v../1.md")]
    public void TryParseLabelFromFileName_rejects_non_sidecar_names_and_clears_label(string fileName)
    {

        bool parsed = SpellVersionPathPolicy.TryParseLabelFromFileName(fileName, out string label);

        Assert.False(parsed);

        Assert.Equal(string.Empty, label);

    }

}
