using System.CommandLine;
using System.CommandLine.Completions;

namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

/// <summary>
/// Projects the live <see cref="RootCommand"/> into <see cref="CliSurfaceMap"/>. Help, shell
/// completion, "did you mean", topic help, and the committed command map are all readers of this
/// projection, so none of them can drift from the tree the parser uses. The walk reads
/// System.CommandLine's own object graph — it performs no assembly scanning and stays AOT-clean.
/// </summary>
internal static class CliSurfaceBuilder
{

    public static CliSurfaceMap Build(Command root)
    {

        ArgumentNullException.ThrowIfNull(root);

        List<CliSurfaceCommand> commands =
        [
            .. root.Subcommands
                .Where(static command => !command.Hidden)
                .Select(command => BuildCommand(command, string.Empty)),
        ];

        List<CliSurfaceOption> globalOptions =
        [
            .. root.Options
                .Where(static option => !option.Hidden)
                .Select(BuildOption),
        ];

        return new CliSurfaceMap(
            globalOptions,
            BuildShortOptionTable(globalOptions, commands),
            commands);

    }

    private static CliSurfaceCommand BuildCommand(Command command, string parentPath)
    {

        string path = parentPath.Length == 0
            ? command.Name
            : $"{parentPath} {command.Name}";

        return new CliSurfaceCommand(
            path,
            command.Name,
            command.Description ?? string.Empty,
            command.Action is not null,
            [.. command.Aliases.OrderBy(static alias => alias, StringComparer.Ordinal)],
            [.. command.Arguments.Where(static argument => !argument.Hidden).Select(BuildArgument)],
            [.. command.Options.Where(static option => !option.Hidden).Select(BuildOption)],
            CliSurfaceExamples.For(path),
            [.. command.Subcommands.Where(static child => !child.Hidden).Select(child => BuildCommand(child, path))]);

    }

    private static CliSurfaceOption BuildOption(Option option)
    {

        bool isFlag = option.ValueType == typeof(bool);

        return new CliSurfaceOption(
            option.Name,
            [.. option.Aliases.OrderBy(static alias => alias, StringComparer.Ordinal)],
            option.Description ?? string.Empty,
            option.Recursive,
            !isFlag && option.Arity.MaximumNumberOfValues > 0,
            option.Arity.MaximumNumberOfValues > 1,
            isFlag ? [] : ClosedValues(option),
            CliCompletionBindings.For(option.Name));

    }

    private static CliSurfaceArgument BuildArgument(Argument argument) =>
        new(
            argument.Name,
            argument.Description ?? string.Empty,
            argument.Arity.MinimumNumberOfValues > 0,
            argument.Arity.MaximumNumberOfValues > 1,
            ClosedValues(argument),
            CliCompletionBindings.For(argument.Name));

    /// <summary>
    /// Only a symbol constrained with <c>AcceptOnlyFromAmong</c> (or a naturally closed value type)
    /// reports a set here. Free-form symbols return nothing rather than a guess, so static
    /// completion never invents a value the parser would reject.
    /// </summary>
    private static IReadOnlyList<string> ClosedValues(Symbol symbol) =>
        [
            .. symbol
                .GetCompletions(CompletionContext.Empty)
                .Select(static item => item.InsertText ?? item.Label)
                .Where(static value => !string.IsNullOrWhiteSpace(value)),
        ];

    private static IReadOnlyList<CliSurfaceShortOption> BuildShortOptionTable(
        IReadOnlyList<CliSurfaceOption> globalOptions,
        IReadOnlyList<CliSurfaceCommand> commands)
    {

        Dictionary<string, SortedSet<string>> scopes = [];

        Dictionary<string, string> meanings = [];

        foreach (CliSurfaceOption option in globalOptions)
        {

            Record(option, "(global)");

        }

        foreach (CliSurfaceCommand command in commands.SelectMany(Flatten))
        {

            foreach (CliSurfaceOption option in command.Options)
            {

                Record(option, command.Path);

            }

        }

        return
        [
            .. meanings
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new CliSurfaceShortOption(
                    entry.Key,
                    entry.Value,
                    [.. scopes[entry.Key]])),
        ];

        void Record(CliSurfaceOption option, string scope)
        {

            foreach (string alias in option.Aliases.Where(IsShort))
            {

                meanings.TryAdd(alias, option.Name);

                if (!scopes.TryGetValue(alias, out SortedSet<string>? recorded))
                {

                    recorded = new SortedSet<string>(StringComparer.Ordinal);

                    scopes[alias] = recorded;

                }

                recorded.Add(scope);

            }

        }

    }

    public static IEnumerable<CliSurfaceCommand> Flatten(CliSurfaceCommand command)
    {

        ArgumentNullException.ThrowIfNull(command);

        yield return command;

        foreach (CliSurfaceCommand child in command.Commands.SelectMany(Flatten))
        {

            yield return child;

        }

    }

    internal static bool IsShort(string alias) =>
        alias.Length == 2 && alias[0] == '-' && alias[1] != '-';

}
