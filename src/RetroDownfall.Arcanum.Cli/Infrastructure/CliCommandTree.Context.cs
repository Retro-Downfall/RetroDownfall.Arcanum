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

        context.Add(BuildContextPreview(handler, "inspect", ContextPreviewView.Inspect));

        context.Add(BuildContextPreview(handler, "tools", ContextPreviewView.Tools));

        context.Add(BuildContextPreview(handler, "sources", ContextPreviewView.Sources));

        return context;

    }

    private static Command BuildMana(IServiceProvider serviceProvider) =>

        BuildContextPreview(

            serviceProvider.GetRequiredService<ContextCommands>(),

            "mana",

            ContextPreviewView.Mana);

    private static Command BuildContextPreview(

        ContextCommands handler,

        string name,

        ContextPreviewView view)

    {

        Command command = new(

            name,

            view == ContextPreviewView.Mana

                ? "Estimate the effective turn token allocation without main inference."

                : $"Inspect effective turn {name} without main inference.");

        Argument<string[]> prompt = new("prompt")

        {

            Arity = ArgumentArity.ZeroOrMore,

            Description = "Optional prompt text used for routing and retrieval.",

        };

        Option<bool> showContent = new("--show-content")

        {

            Description = "Include model-visible content for explicit operator inspection.",

        };

        Option<bool> noRetrieval = new("--no-retrieval")

        {

            Description = "Skip embedding and RAG retrieval work.",

        };

        Option<string?> campaign = new("--campaign", "-c")

        {

            Description = "Campaign GUID or name; defaults to saved/detected context.",

        };

        Option<string?> workspace = new("--workspace", "-w")

        {

            Description = "Workspace ID or path; defaults to saved/detected context.",

        };

        Option<string?> model = new("--model", "-m")

        {

            Description = "Model name; defaults to saved/server context.",

        };

        Option<string?> session = new("--session", "-s")

        {

            Description = "Session GUID, title, or prefix; defaults to saved context.",

        };

        command.Add(prompt);

        command.Add(showContent);

        command.Add(noRetrieval);

        command.Add(campaign);

        command.Add(workspace);

        command.Add(model);

        command.Add(session);

        command.SetAction(

            async (ParseResult parseResult, CancellationToken cancellationToken) =>

                await handler.Preview(

                    view,

                    string.Join(' ', parseResult.GetValue(prompt) ?? []),

                    parseResult.GetValue(showContent),

                    parseResult.GetValue(noRetrieval),

                    parseResult.GetValue(campaign),

                    parseResult.GetValue(workspace),

                    parseResult.GetValue(model),

                    parseResult.GetValue(session),

                    cancellationToken).ConfigureAwait(false));

        return command;

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
