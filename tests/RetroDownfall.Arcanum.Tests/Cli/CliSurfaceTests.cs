using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The command surface is projected from the live <see cref="RootCommand"/> rather than
/// re-declared, so help, completion, the committed command map, and the alias-absence tests all
/// observe the same tree the parser observes.
/// </summary>
public sealed class CliSurfaceTests
{

    [Fact]
    public void Surface_projects_the_live_tree_root()
    {

        CliSurfaceMap map = BuildMap();

        Assert.NotEmpty(map.Commands);

        Assert.Contains(map.Commands, command => command.Path == "run");

        Assert.Contains(map.Commands, command => command.Path == "doctor");

    }

    [Fact]
    public void Every_command_carries_help_text()
    {

        List<string> missing =
        [
            .. Walk(BuildMap())
                .Where(static command => string.IsNullOrWhiteSpace(command.Description))
                .Select(static command => command.Path),
        ];

        Assert.Empty(missing);

    }

    [Fact]
    public void Every_option_and_argument_carries_help_text()
    {

        List<string> missing = [];

        foreach (CliSurfaceCommand command in Walk(BuildMap()))
        {

            missing.AddRange(
                command.Options
                    .Where(static option => string.IsNullOrWhiteSpace(option.Description))
                    .Select(option => $"{command.Path} {option.Name}"));

            missing.AddRange(
                command.Arguments
                    .Where(static argument => string.IsNullOrWhiteSpace(argument.Description))
                    .Select(argument => $"{command.Path} <{argument.Name}>"));

        }

        Assert.Empty(missing);

    }

    /// <summary>
    /// Claude parity does not justify ambiguous parsing: a short flag means exactly one thing
    /// everywhere in the tree, so <c>-c</c> cannot be <c>--continue</c> here and <c>--campaign</c>
    /// there.
    /// </summary>
    [Fact]
    public void Short_options_have_exactly_one_meaning_across_the_whole_tree()
    {

        Dictionary<string, string> meanings = [];

        List<string> collisions = [];

        foreach (CliSurfaceCommand command in Walk(BuildMap()))
        {

            foreach (CliSurfaceOption option in command.Options)
            {

                foreach (string alias in option.Aliases.Where(IsShort))
                {

                    if (meanings.TryGetValue(alias, out string? existing)
                        && existing != option.Name)
                    {

                        collisions.Add($"{alias} means {existing} and {option.Name} ({command.Path})");

                        continue;

                    }

                    meanings[alias] = option.Name;

                }

            }

        }

        Assert.Empty(collisions);

    }

    [Fact]
    public void Short_option_table_publishes_the_claude_aligned_meanings()
    {

        Dictionary<string, string> table = BuildMap()
            .ShortOptions
            .ToDictionary(static entry => entry.Alias, static entry => entry.Option, StringComparer.Ordinal);

        Assert.Equal("--continue", table["-c"]);

        Assert.Equal("--campaign", table["-C"]);

        Assert.Equal("--resume", table["-r"]);

        Assert.Equal("--print", table["-p"]);

        Assert.Equal("--verbose", table["-v"]);

        Assert.Equal("--model", table["-m"]);

        Assert.Equal("--session", table["-s"]);

        Assert.Equal("--workspace", table["-w"]);

    }

    /// <summary>
    /// <c>docs/Arcanum.CommandMap.json</c> is the committed, machine-readable command contract.
    /// Regenerate it with <c>ARCANUM_UPDATE_COMMAND_MAP=1 dotnet test --filter
    /// Committed_command_map_matches_the_live_tree</c> and review the diff: an unintended entry in
    /// that diff is an unintended change to the public CLI surface.
    /// </summary>
    [Fact]
    public void Committed_command_map_matches_the_live_tree()
    {

        string path = CommandMapPath();

        string actual = CliSurfaceWriter.ToJson(BuildMap()).ReplaceLineEndings("\n");

        if (global::System.Environment.GetEnvironmentVariable("ARCANUM_UPDATE_COMMAND_MAP") == "1")
        {

            File.WriteAllText(path, actual);

        }

        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");

        Assert.Equal(expected, actual);

    }

    [Fact]
    public void Command_map_generation_is_byte_for_byte_stable()
    {

        Assert.Equal(
            CliSurfaceWriter.ToJson(BuildMap()),
            CliSurfaceWriter.ToJson(BuildMap()));

    }

    internal static CliSurfaceMap BuildMap()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        return CliSurfaceBuilder.Build(root);

    }

    internal static IEnumerable<CliSurfaceCommand> Walk(CliSurfaceMap map) =>
        map.Commands.SelectMany(Walk);

    internal static IEnumerable<CliSurfaceCommand> Walk(CliSurfaceCommand command)
    {

        yield return command;

        foreach (CliSurfaceCommand child in command.Commands.SelectMany(Walk))
        {

            yield return child;

        }

    }

    internal static string CommandMapPath()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
        {

            directory = directory.Parent;

        }

        Assert.NotNull(directory);

        return Path.Combine(directory.FullName, "docs", "Arcanum.CommandMap.json");

    }

    private static bool IsShort(string alias) =>
        alias.Length == 2 && alias[0] == '-' && alias[1] != '-';

}
