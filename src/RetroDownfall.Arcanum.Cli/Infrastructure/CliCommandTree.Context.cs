using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildUse(IServiceProvider serviceProvider)
    {

        ContextCommands handler =
            serviceProvider.GetRequiredService<ContextCommands>();

        Command use = new(
            "use",
            "Select persistent local CLI context defaults.");

        use.Add(BuildUseResource(handler, "campaign", CliContextScope.Campaign, "campaign ID or name"));

        use.Add(BuildUseResource(handler, "workspace", CliContextScope.Workspace, "workspace ID or path"));

        use.Add(BuildUseResource(handler, "model", CliContextScope.Model, "model name"));

        use.Add(BuildUseResource(handler, "session", CliContextScope.Session, "session ID"));

        Command clear = new(
            "clear",
            "Clear all saved context or one scope.");

        Argument<string?> scope = new("scope")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Optional scope: campaign, workspace, model, or session.",
        };

        clear.Add(scope);

        clear.SetAction((ParseResult parseResult) =>
        {

            if (!TryParseScope(
                    parseResult.GetValue(scope),
                    out CliContextScope parsedScope))
            {

                return handler.InvalidClearScope(
                    parseResult.GetValue(scope));

            }

            return handler.Clear(parsedScope);

        });

        use.Add(clear);

        return use;

    }

    private static Command BuildContext(IServiceProvider serviceProvider)
    {

        ContextCommands handler =
            serviceProvider.GetRequiredService<ContextCommands>();

        Command context = new(
            "context",
            "Inspect effective CLI context and its sources.");

        Command current = new(
            "current",
            "Show effective campaign, workspace, model, and session context.");

        current.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler.Current(cancellationToken).ConfigureAwait(false));

        context.Add(current);

        return context;

    }

    private static Command BuildUseResource(
        ContextCommands handler,
        string name,
        CliContextScope scope,
        string description)
    {

        Command command = new(name, $"Select an active {name}.");

        Argument<string> identifier = new("identifier")
        {
            Description = description,
        };

        command.Add(identifier);

        command.SetAction(
            async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await handler.Use(
                    scope,
                    parseResult.GetValue(identifier)!,
                    cancellationToken).ConfigureAwait(false));

        return command;

    }

    private static bool TryParseScope(
        string? value,
        out CliContextScope scope)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            scope = CliContextScope.All;

            return true;

        }

        return Enum.TryParse(value, ignoreCase: true, out scope)
            && scope != CliContextScope.All;

    }

}
