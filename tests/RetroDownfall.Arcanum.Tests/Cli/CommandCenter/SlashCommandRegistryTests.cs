using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// One registry owns every slash name, its help text, and the canonical replacement for each
/// removed spelling. Help rendering, parsing, and "did you mean" all read it, so a name cannot be
/// documented in one place and handled in another.
/// </summary>
public sealed class SlashCommandRegistryTests
{

    [Theory]
    [InlineData("help")]
    [InlineData("clear")]
    [InlineData("compact")]
    [InlineData("config")]
    [InlineData("context")]
    [InlineData("cost")]
    [InlineData("doctor")]
    [InlineData("memory")]
    [InlineData("model")]
    [InlineData("resume")]
    [InlineData("status")]
    [InlineData("mcp")]
    [InlineData("exit")]
    [InlineData("quit")]
    public void Claude_aligned_names_are_registered(string name)
    {

        Assert.True(
            SlashCommandRegistry.TryResolve(name, out SlashCommandDescriptor? descriptor),
            $"/{name} is not registered.");

        Assert.False(string.IsNullOrWhiteSpace(descriptor!.Description));

    }

    [Theory]
    [InlineData("mana", "/context")]
    [InlineData("new", "/clear")]
    [InlineData("summary", "/memory")]
    [InlineData("rest", "/compact")]
    [InlineData("history", "/session list")]
    [InlineData("delete", "/session archive")]
    public void Removed_slash_names_name_their_canonical_replacement(string removed, string canonical)
    {

        Assert.True(
            SlashCommandRegistry.TryResolveRemoved(removed, out string? replacement),
            $"/{removed} has no recorded replacement.");

        Assert.Equal(canonical, replacement);

        Assert.False(
            SlashCommandRegistry.TryResolve(removed, out _),
            $"/{removed} should no longer resolve.");

    }

    [Fact]
    public void Every_registered_name_is_unique_across_names_and_aliases()
    {

        List<string> spellings =
        [
            .. SlashCommandRegistry.All.SelectMany(
                static descriptor => descriptor.Aliases.Append(descriptor.Name)),
        ];

        Assert.Equal(spellings.Count, spellings.Distinct(StringComparer.Ordinal).Count());

    }

    [Fact]
    public void Removed_names_never_collide_with_a_live_name()
    {

        foreach (string removed in SlashCommandRegistry.RemovedNames)
        {

            Assert.False(
                SlashCommandRegistry.TryResolve(removed, out _),
                $"/{removed} is listed as removed but still resolves.");

        }

    }

    [Theory]
    [InlineData("contex", "/context")]
    [InlineData("stat", "/status")]
    [InlineData("hlep", "/help")]
    [InlineData("cost1", "/cost")]
    public void Near_misses_suggest_the_nearest_canonical_name(string typo, string expected)
    {

        Assert.Equal(expected, SlashCommandRegistry.Suggest(typo));

    }

    [Fact]
    public void An_unrelated_word_gets_no_suggestion_rather_than_a_wrong_one()
    {

        Assert.Null(SlashCommandRegistry.Suggest("zzzzzzzzzzzz"));

    }

    /// <summary>
    /// Help is rendered from the registry, so a newly registered command cannot ship undocumented.
    /// </summary>
    [Fact]
    public void Help_text_lists_every_registered_command()
    {

        string help = SlashCommandRegistry.BuildHelpText();

        foreach (SlashCommandDescriptor descriptor in SlashCommandRegistry.All)
        {

            Assert.Contains($"/{descriptor.Name}", help, StringComparison.Ordinal);

        }

    }

    /// <summary>
    /// <c>/help</c> renders <see cref="SlashCommandDescriptor.Usage"/> verbatim, so every alternative
    /// spelled out in a <c>a|b|c</c> group has to be one the parser still accepts. Documenting a form
    /// the grammar denies makes the built-in help the only place teaching a removed command.
    /// </summary>
    [Fact]
    public void Every_documented_alternative_is_accepted_by_the_parser()
    {

        ShellCommandParser parser = new();

        const string sampleId = "11111111-1111-1111-1111-111111111111";

        foreach (SlashCommandDescriptor descriptor in SlashCommandRegistry.All)
        {

            if (!SlashUsageExpander.StripOptionalGroups(descriptor.Usage).Contains('|', StringComparison.Ordinal))
            {

                continue;

            }

            foreach (string form in SlashUsageExpander.Expand(descriptor.Usage, sampleId))
            {

                ParsedShellCommand parsed = parser.Parse(form);

                Assert.True(
                    parsed.Kind is not (ShellCommandKind.Denied or ShellCommandKind.Unknown),
                    $"`{descriptor.Usage}` documents `{form}`, which the parser rejects: {parsed.DenialMessage}");

            }

        }

    }

}
