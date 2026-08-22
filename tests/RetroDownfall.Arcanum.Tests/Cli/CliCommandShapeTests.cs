using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Exactly one spelling resolves each action. Arcanum ships no compatibility layer, so a removed
/// alias must actually fail to parse rather than quietly continuing to work.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliCommandShapeTests
{

    /// <summary>
    /// Families whose single-resource read is <c>show</c>. <c>config get</c> and <c>lore get</c> are
    /// deliberately absent: those read a value by key rather than a resource by identity, and
    /// <c>get</c> is the Claude Code spelling for that shape.
    /// </summary>
    private static readonly string[] ShowFamilies =
    [
        "campaign",
        "session",
        "spell",
        "prompt",
        "model",
        "provider",
        "mcp",
        "workspace",
        "apprentice",
        "ward",
    ];

    [Theory]
    [InlineData("session", "get")]
    [InlineData("workspace", "get")]
    [InlineData("mcp", "get")]
    [InlineData("campaign", "get")]
    [InlineData("spell", "get")]
    [InlineData("prompt", "get")]
    [InlineData("apprentice", "get")]
    [InlineData("ward", "get")]
    [InlineData("model", "get")]
    [InlineData("provider", "get")]
    [InlineData("session", "watch")]
    [InlineData("apprentice", "chronicle")]
    [InlineData("campaign", "use")]
    public void Removed_subcommand_aliases_do_not_resolve(string family, string verb)
    {

        // Deliberately no --help: the help action short-circuits before unmatched-token errors, so
        // asserting against `<family> <verb> --help` would pass even if the verb still existed.
        CliTestResult result = CliTestHarness.Run(CreateServices(), family, verb);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

    }

    [Fact]
    public void Removed_top_level_mana_command_does_not_resolve()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "mana");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

    }

    [Fact]
    public void Every_single_resource_read_is_spelled_show()
    {

        Dictionary<string, CliSurfaceCommand> commands = AllCommands();

        foreach (string family in ShowFamilies)
        {

            Assert.True(
                commands.ContainsKey($"{family} show"),
                $"{family} show is missing.");

            Assert.False(
                commands.ContainsKey($"{family} get"),
                $"{family} get should have been removed.");

        }

    }

    [Fact]
    public void Use_is_the_only_active_context_selector()
    {

        Dictionary<string, CliSurfaceCommand> commands = AllCommands();

        Assert.True(commands.ContainsKey("use campaign"));

        Assert.True(commands.ContainsKey("use workspace"));

        Assert.True(commands.ContainsKey("use model"));

        Assert.True(commands.ContainsKey("use session"));

        Assert.DoesNotContain(
            commands.Keys,
            static path => path.EndsWith(" use", StringComparison.Ordinal));

    }

    [Fact]
    public void Watch_is_the_only_live_stream_entry()
    {

        List<string> streams =
        [
            .. AllCommands()
                .Keys
                .Where(static path =>
                    path.EndsWith(" watch", StringComparison.Ordinal)
                    || path.EndsWith(" chronicle", StringComparison.Ordinal)),
        ];

        Assert.Empty(streams);

        Assert.Contains("watch session", AllCommands().Keys);

    }

    [Fact]
    public void No_command_declares_a_parser_level_alias()
    {

        List<string> aliased =
        [
            .. CliSurfaceTests
                .Walk(CliSurfaceTests.BuildMap())
                .Where(static command => command.Aliases.Count > 0)
                .Select(static command => command.Path),
        ];

        Assert.Empty(aliased);

    }

    /// <summary>
    /// Long options keep exactly one spelling too. camelCase duplicates and second names for the
    /// same concept were pure surface cost. This asserts the removed spellings are gone as
    /// <em>aliases</em>; <c>file upload --content-type</c> keeps that name as its own primary
    /// option, which is a different concept from an attachment's <c>--mime</c>.
    /// </summary>
    [Theory]
    [InlineData("--tool-name")]
    [InlineData("--campaignId")]
    [InlineData("--sessionId")]
    [InlineData("--agentUrl")]
    [InlineData("--content-type")]
    public void Removed_option_aliases_are_absent_from_the_whole_tree(string alias)
    {

        List<string> declaring =
        [
            .. CliSurfaceTests
                .Walk(CliSurfaceTests.BuildMap())
                .Where(command => command.Options.Any(
                    option => option.Aliases.Contains(alias, StringComparer.Ordinal)))
                .Select(static command => command.Path),
        ];

        Assert.Empty(declaring);

    }

    /// <summary>
    /// No long option may be spelled in camelCase anywhere. This is the general rule the removed
    /// <c>--campaignId</c>/<c>--sessionId</c>/<c>--agentUrl</c> pairs were instances of.
    /// </summary>
    [Fact]
    public void Long_options_are_kebab_case_everywhere()
    {

        List<string> offenders = [];

        foreach (CliSurfaceCommand command in CliSurfaceTests.Walk(CliSurfaceTests.BuildMap()))
        {

            offenders.AddRange(
                command.Options
                    .SelectMany(option => option.Aliases.Append(option.Name))
                    .Where(static spelling =>
                        spelling.StartsWith("--", StringComparison.Ordinal)
                        && spelling.Any(char.IsUpper))
                    .Select(spelling => $"{command.Path} {spelling}"));

        }

        Assert.Empty(offenders);

    }

    [Fact]

    public void Factory_reset_has_the_exact_reduced_option_surface()
    {

        CliSurfaceCommand command = AllCommands()["data factory-reset"];

        Assert.Equal(
            [
                "--all",
                "--apply",
                "--dry-run",
                "--external-remediation-attestation",
                "--force",
                "--global",
                "--workspace",
            ],
            command.Options
                .Select(static option => option.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

        Assert.Equal(
            ["arcanum data factory-reset --all --dry-run"],
            command.Examples);

    }

    private static Dictionary<string, CliSurfaceCommand> AllCommands() =>
        CliSurfaceTests
            .Walk(CliSurfaceTests.BuildMap())
            .ToDictionary(static command => command.Path, static command => command, StringComparer.Ordinal);

    private static ServiceCollection CreateServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        return services;

    }

}
