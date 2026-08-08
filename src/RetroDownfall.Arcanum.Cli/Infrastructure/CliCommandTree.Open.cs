using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildCenter(IServiceProvider services) =>
        CommandCenterCommand(services);

    /// <summary>
    /// Command Center is the interactive turn entry, so it carries the same continuation contract as
    /// the one-shot entry: <c>-c</c> reopens the most recent Session and <c>-r</c> reopens a named
    /// one, rather than requiring a separate management verb to get back into a thread.
    /// </summary>
    private static Command CommandCenterCommand(IServiceProvider services)
    {

        OpenCommands handler = services.GetRequiredService<OpenCommands>();

        Command center = new(
            "center",
            "Open Command Center in the current arcanum process.");

        Option<bool> continueSession = new("--continue", "-c")
        {

            Description = "Reopen the most recent Session. Cannot be combined with --resume.",

        };

        Option<string?> resume = new("--resume", "-r")
        {

            Arity = ArgumentArity.ZeroOrOne,

            Description = "Reopen a Session by GUID, exact title, or unique title prefix; omit the value for an interactive picker.",

        };

        center.Add(continueSession);

        center.Add(resume);

        center.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .Center(
                        result.GetValue(continueSession),
                        result.GetResult(resume) is not null,
                        result.GetValue(resume),
                        cancellationToken)
                    .ConfigureAwait(false));

        return center;

    }

    private static Command BuildOpen(IServiceProvider services)
    {

        OpenCommands handler = services.GetRequiredService<OpenCommands>();

        Command open = new(
            "open",
            "Open an Arcanum application, optionally at a selected resource.");

        Command center = CommandCenterCommand(services);

        Command theForge = new(
            "theforge",
            "Open The Forge.");

        theForge.SetAction((ParseResult _) => handler.TheForge());

        Command compendium = new(
            "compendium",
            "Open Compendium at configuration settings.");

        compendium.SetAction((ParseResult _) => handler.Compendium());

        Command session = ResourceCommand(
            "session",
            "Open The Forge Workbench at a Session.",
            "session",
            "Session GUID, exact title, or unique title prefix.",
            handler.Session);

        Command campaign = ResourceCommand(
            "campaign",
            "Open The Forge Atelier at a Campaign.",
            "campaign",
            "Campaign GUID, exact name, or unique name prefix.",
            handler.Campaign);

        Command spell = new(
            "spell",
            "Open The Forge Workbench at a Spell.");

        Argument<string?> spellSelector = OptionalSelector(
            "spell",
            "Spell exact name or unique name prefix.");

        Option<string?> spellWorkspace = new("--workspace")
        {

            Description =
                "Server Workspace ID, exact name, unique prefix, or server-host path.",

        };

        spell.Add(spellSelector);

        spell.Add(spellWorkspace);

        spell.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler
                    .Spell(
                        result.GetValue(spellSelector),
                        ActiveWorkspace(
                            services,
                            result.GetValue(spellWorkspace)),
                        cancellationToken)
                    .ConfigureAwait(false));

        Command prompt = ResourceCommand(
            "prompt",
            "Open The Forge Workbench at a Prompt.",
            "prompt",
            "Prompt GUID, exact name, or unique name prefix.",
            handler.Prompt);

        Command apprentice = ResourceCommand(
            "apprentice",
            "Open The Forge War Table at an Apprentice.",
            "apprentice",
            "Apprentice GUID, exact name, or unique name prefix.",
            handler.Apprentice);

        open.Add(center);

        open.Add(theForge);

        open.Add(compendium);

        open.Add(session);

        open.Add(campaign);

        open.Add(spell);

        open.Add(prompt);

        open.Add(apprentice);

        return open;

    }

    private static Command ResourceCommand(
        string name,
        string description,
        string argumentName,
        string argumentDescription,
        Func<string?, CancellationToken, Task<int>> action)
    {

        Command command = new(name, description);

        Argument<string?> selector = OptionalSelector(
            argumentName,
            argumentDescription);

        command.Add(selector);

        command.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await action(
                        result.GetValue(selector),
                        cancellationToken)
                    .ConfigureAwait(false));

        return command;

    }

    private static Argument<string?> OptionalSelector(
        string name,
        string description) =>
        new(name)
        {

            Arity = ArgumentArity.ZeroOrOne,

            Description = description + " Omit for the interactive picker.",

        };

}
