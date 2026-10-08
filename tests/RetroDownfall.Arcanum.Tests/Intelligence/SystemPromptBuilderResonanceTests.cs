using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Trait("Suite", "Dci")]
public sealed class SystemPromptBuilderResonanceTests
{
    [Fact]
    public void Build_IncludesResonantSpellsSection_WithBodiesAndScripts()
    {
        ParsedSpell primary = new(
            "Primary",
            "desc",
            "/primary/SPELL.md",
            "---\nname: Primary\n---\nfull",
            "/primary",
            ["run.sh"])
        {
            Body = "primary body",
        };

        ParsedSpell dep = new(
            "DepSpell",
            "dep",
            "/dep/SPELL.md",
            "---\nname: DepSpell\n---\nfull dep",
            "/dep",
            ["analyze.py"])
        {
            Body = "dependency markdown body",
        };

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: null,
            activeSpell: primary,
            dependencySpells: [dep]);

        Assert.Contains("### Resonant Spells (Dependencies)", prompt, StringComparison.Ordinal);

        Assert.Contains("#### DepSpell", prompt, StringComparison.Ordinal);

        Assert.Contains("dependency markdown body", prompt, StringComparison.Ordinal);

        Assert.Contains("analyze.py", prompt, StringComparison.Ordinal);

        Assert.Contains("run_spell_script", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithoutDependencies_OmitsResonantSection()
    {
        ParsedSpell primary = new(
            "Primary",
            "desc",
            "/primary/SPELL.md",
            "---\nname: Primary\n---\nfull",
            "/primary",
            [])
        {
            Body = "primary body",
        };

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: null,
            activeSpell: primary,
            dependencySpells: null);

        Assert.DoesNotContain("### Resonant Spells (Dependencies)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TruncatesResonantBodies_WhenOverByteBudget()
    {
        ParsedSpell primary = new(
            "Primary",
            "desc",
            "/primary/SPELL.md",
            "---\nname: Primary\n---\nfull",
            "/primary",
            [])
        {
            Body = "primary body",
        };

        ParsedSpell dep = new(
            "DepSpell",
            "dep",
            "/dep/SPELL.md",
            "---\nname: DepSpell\n---\nfull dep",
            "/dep",
            [])
        {
            Body = new string('x', 200),
        };

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: null,
            activeSpell: primary,
            dependencySpells: [dep],
            maxResonantBytes: 32);

        Assert.Contains("exceeded the configured byte budget", prompt, StringComparison.Ordinal);

        Assert.DoesNotContain(new string('x', 200), prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    public void Build_TruncatesResonantBody_NeverSplitsAnAstralCharacter(int maxResonantBytes)
    {
        ParsedSpell primary = new(
            "Primary",
            "desc",
            "/primary/SPELL.md",
            "---\nname: Primary\n---\nfull",
            "/primary",
            [])
        {
            Body = "primary body",
        };

        ParsedSpell dep = new(
            "DepSpell",
            "dep",
            "/dep/SPELL.md",
            "---\nname: DepSpell\n---\nfull dep",
            "/dep",
            [])
        {
            Body = "ab" + string.Concat(Enumerable.Repeat("\U0001F600", 20)),
        };

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("hello"),
            codexContent: null,
            activeSpell: primary,
            dependencySpells: [dep],
            maxResonantBytes: maxResonantBytes);

        // A UTF-8 encoder that throws on a lone surrogate is the oracle: a half of the emoji left behind by a
        // byte cut that counted the lone half as a three-byte replacement character cannot be encoded.
        UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        _ = strict.GetBytes(prompt);

        int emojiCount = CountOccurrences(prompt, "\U0001F600");

        Assert.Equal((maxResonantBytes - 2) / 4, emojiCount);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;

        int index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;

            index += value.Length;
        }

        return count;
    }
}
