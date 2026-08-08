using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    /// <summary>
    /// Builds the completion family. The surface map is captured lazily from the same root the
    /// parser uses, so a generated script can never describe a tree the binary does not have.
    /// </summary>
    private static Command BuildCompletion(
        IServiceProvider serviceProvider,
        Func<CliSurfaceMap> surface)
    {

        CompletionCommands handler = serviceProvider.GetRequiredService<CompletionCommands>();

        Command completion = new(
            "completion",
            "Generate shell completion for bash, zsh, fish, or powershell.");

        Argument<string> shell = ShellArgument();

        completion.Add(shell);

        completion.SetAction((ParseResult result) =>
            handler.Generate(result.GetValue(shell)!, surface()));

        Command install = new(
            "install",
            "Write the completion script to this shell's conventional location after confirmation.");

        Argument<string> installShell = ShellArgument();

        Option<string?> target = new("--target")
        {

            Description = "Explicit destination path; defaults to the shell's conventional per-user completion location.",

        };

        install.Add(installShell);

        install.Add(target);

        install.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .InstallAsync(
                        result.GetValue(installShell)!,
                        result.GetValue(target),
                        surface(),
                        cancellationToken)
                    .ConfigureAwait(false));

        completion.Add(install);

        // Called by the generated scripts on every relevant keystroke, not by operators. It is
        // hidden so it cannot clutter help or be suggested, and it prints nothing when the host is
        // unavailable.
        Command resolve = new(
            "resolve",
            "Resolve one safe dynamic completion source for the generated shell scripts.")
        {

            Hidden = true,

        };

        Argument<string> provider = new("provider")
        {

            Description = "Completion source id emitted by the generated script.",

        };

        provider.AcceptOnlyFromAmong(CliCompletionProviders.All);

        resolve.Add(provider);

        resolve.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .ResolveAsync(result.GetValue(provider)!, cancellationToken)
                    .ConfigureAwait(false));

        completion.Add(resolve);

        return completion;

    }

    private static Argument<string> ShellArgument()
    {

        Argument<string> shell = new("shell")
        {

            Description = "Target shell: bash, zsh, fish, or powershell.",

        };

        shell.AcceptOnlyFromAmong(CliCompletionShells.Names);

        return shell;

    }

    /// <summary>
    /// Topic help. <c>--help</c> answers "what are this command's options"; <c>help &lt;topic&gt;</c>
    /// answers "how do I do X", and glosses the thematic vocabulary in plain language so an
    /// operator who has never read the metaphor table can still navigate.
    /// </summary>
    private static Command BuildHelpTopics(IServiceProvider serviceProvider)
    {

        HelpTopicCommands handler = serviceProvider.GetRequiredService<HelpTopicCommands>();

        Command help = new(
            "help",
            "Explain a task-oriented topic in plain language, with the commands that do it.");

        Argument<string?> topic = new("topic")
        {

            Arity = ArgumentArity.ZeroOrOne,

            Description = $"Topic name; omit to list every topic. Available: {string.Join(", ", HelpTopics.Names)}.",

        };

        topic.AcceptOnlyFromAmong(HelpTopics.Names);

        help.Add(topic);

        help.SetAction((ParseResult result) =>
            handler.Show(result.GetValue(topic)));

        return help;

    }

}
