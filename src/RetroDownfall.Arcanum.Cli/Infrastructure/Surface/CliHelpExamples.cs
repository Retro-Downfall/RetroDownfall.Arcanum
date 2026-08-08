using System.CommandLine;

namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

/// <summary>
/// Appends an <c>Examples:</c> section to <c>--help</c>.
///
/// The examples themselves live in <see cref="CliSurfaceExamples"/> and are parse-tested against
/// the live tree, so a renamed verb or removed option breaks the build rather than shipping help
/// that no longer works. This is the rendering half: without it the examples would be documented
/// for tooling and the command map, and invisible to the operator actually typing <c>--help</c>.
///
/// This runs after the invocation instead of replacing the help action. System.CommandLine 2.0.10
/// seals <c>HelpAction</c>, keeps <c>HelpContext</c> internal so the layout hook is unusable from
/// here, and exposes <c>Terminating</c>/<c>ClearsParseErrors</c> as read-only. A wrapper action can
/// therefore not reproduce the flag that makes <c>--help</c> exit 0 on a command whose required
/// argument the caller has not supplied — appending afterwards leaves that contract untouched.
/// </summary>
internal static class CliHelpExamples
{

    public static void Append(
        Command root,
        ParseResult parseResult,
        IReadOnlyList<string> arguments,
        TextWriter output)
    {

        ArgumentNullException.ThrowIfNull(root);

        ArgumentNullException.ThrowIfNull(parseResult);

        ArgumentNullException.ThrowIfNull(arguments);

        ArgumentNullException.ThrowIfNull(output);

        if (!RequestsHelp(arguments))
        {

            return;

        }

        if (!BuildPaths(root).TryGetValue(parseResult.CommandResult.Command, out string? path))
        {

            return;

        }

        IReadOnlyList<string> examples = CliSurfaceExamples.For(path);

        if (examples.Count > 0)
        {

            output.WriteLine("Examples:");

            foreach (string example in examples)
            {

                output.WriteLine($"  {example}");

            }

            output.WriteLine();

            return;

        }

        // A command that deliberately has no safe example says so, rather than looking like it was
        // simply forgotten. A grouping command with neither prints nothing at all.
        if (CliSurfaceExamples.ExemptionReason(path) is { } reason)
        {

            output.WriteLine("Examples:");

            output.WriteLine($"  No example is shown. {reason}");

            output.WriteLine();

        }

    }

    private static bool RequestsHelp(IReadOnlyList<string> arguments) =>
        arguments.Any(static argument =>
            argument is "--help" or "-h" or "-?" or "/?" or "/h");

    /// <summary>
    /// Maps each command instance to its canonical space-separated path. Built by walking the tree
    /// downward, because System.CommandLine does not expose parent traversal outside its own
    /// assembly. The root's own name is the executable name and is not part of a command path.
    /// </summary>
    private static Dictionary<Command, string> BuildPaths(Command root)
    {

        Dictionary<Command, string> paths = [];

        Queue<(Command Command, string Parent)> pending = new();

        pending.Enqueue((root, string.Empty));

        while (pending.Count > 0)
        {

            (Command command, string parent) = pending.Dequeue();

            foreach (Command child in command.Subcommands)
            {

                string path = parent.Length == 0
                    ? child.Name
                    : $"{parent} {child.Name}";

                paths[child] = path;

                pending.Enqueue((child, path));

            }

        }

        return paths;

    }

}
