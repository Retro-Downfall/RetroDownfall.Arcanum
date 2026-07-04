using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

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

}
