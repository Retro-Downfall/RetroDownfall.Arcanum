using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    /// <summary>
    /// Refuses an option spelling that a free-text prompt positional swallowed, returning the exit
    /// code the action must hand back, or <c>null</c> when the prompt is really prompt text.
    ///
    /// The check lives here rather than in an argument validator because only the full
    /// <see cref="ParseResult"/> carries the <c>--</c> terminator: System.CommandLine routes the
    /// tokens after it into the prompt and drops the separator itself, so a validator cannot tell
    /// deliberate dash-led prompt text from a typo. Exit 2 matches every other invalid command
    /// line, and no turn — billed inference, real tool calls — is dispatched.
    /// </summary>
    private static int? RejectedPromptOption(
        IServiceProvider serviceProvider,
        ParseResult parseResult,
        Argument prompt)
    {

        if (CliSuggestionEngine.DescribePromptOption(parseResult, prompt) is not string diagnostic)
        {

            return null;

        }

        IConsoleDispatcher dispatcher =
            serviceProvider.GetRequiredService<IConsoleDispatcher>();

        dispatcher.WriteDiagnostic(diagnostic);

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                new CliErrorPayload(
                    diagnostic,
                    (int)CliExitCode.ConfigurationError),
                CliJsonContext.Default.CliErrorPayload);

        }

        return (int)CliExitCode.ConfigurationError;

    }

}
