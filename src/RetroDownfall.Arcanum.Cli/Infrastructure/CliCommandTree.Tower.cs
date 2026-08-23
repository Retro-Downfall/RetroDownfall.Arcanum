using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands.Tower;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildPrompt(IServiceProvider sp)
    {
        PromptCommands handler = sp.GetRequiredService<PromptCommands>();
        Command prompt = new("prompt", "Prompt utilities (requires arcanum serve).");

        Command list = new("list", "List prompts.");
        Option<string?> listCampaignId = new("--campaign-id") { Description = "Filter by campaign GUID." };
        Option<string?> listQuery = new("--query", "-q") { Description = "Free-text query." };
        Option<string?> listTag = new("--tag") { Description = "Filter by tag." };
        list.Add(listCampaignId); list.Add(listQuery); list.Add(listTag);
        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(ActiveCampaign(sp, pr.GetValue(listCampaignId)), pr.GetValue(listQuery), pr.GetValue(listTag), ct).ConfigureAwait(false));
        prompt.Add(list);

        Command show = new("show", "Show prompt detail.");
        Argument<string?> showId = new("prompt-name")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Optional prompt GUID, exact name, or unique name prefix; omit for an interactive picker.",
        };
        show.Add(showId);
        show.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(showId), ct).ConfigureAwait(false));
        prompt.Add(show);

        Command versions = new("versions", "List versions of a prompt by name.");
        Argument<string> versionsName = new("name") { Description = "Prompt name." };
        Option<string?> versionsCampaignId = new("--campaign-id") { Description = "Filter by campaign GUID." };
        versions.Add(versionsName); versions.Add(versionsCampaignId);
        versions.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Versions(pr.GetValue(versionsName)!, ActiveCampaign(sp, pr.GetValue(versionsCampaignId)), ct).ConfigureAwait(false));
        prompt.Add(versions);

        Command create = new("create", "Create a prompt.");
        Option<string?> createName = new("--name") { Description = "Prompt name." };
        Option<string?> createVersion = new("--version") { Description = "Prompt version label." };
        Option<string?> createTemplate = new("--template") { Description = "Prompt template: inline text, or @filename to read from a file." };
        Option<string?> createCampaignId = new("--campaign-id") { Description = "Campaign GUID to associate with." };
        Option<string?> createDescription = new("--description") { Description = "Prompt description." };
        Option<string[]> createTag = new("--tag") { AllowMultipleArgumentsPerToken = true, Description = "Tag; pass multiple times for several tags." };
        create.Add(createName); create.Add(createVersion); create.Add(createTemplate);
        create.Add(createCampaignId); create.Add(createDescription); create.Add(createTag);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createName),
                pr.GetValue(createVersion),
                pr.GetValue(createTemplate),
                ActiveCampaign(sp, pr.GetValue(createCampaignId)),
                pr.GetValue(createDescription),
                pr.GetValue(createTag),
                ct).ConfigureAwait(false));
        prompt.Add(create);

        Command update = new("update", "Update a prompt.");
        Argument<string?> updateId = OptionalResourceArgument("id", "prompt GUID or name");
        Option<string?> updateTemplate = new("--template") { Description = "Prompt template: inline text, or @filename to read from a file." };
        Option<string[]> updateTag = new("--tag") { AllowMultipleArgumentsPerToken = true, Description = "Tag; pass multiple times for several tags." };
        update.Add(updateId); update.Add(updateTemplate); update.Add(updateTag);
        update.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Update(pr.GetValue(updateId), pr.GetValue(updateTemplate), pr.GetValue(updateTag), ct).ConfigureAwait(false));
        prompt.Add(update);

        Command delete = new("delete", "Delete a prompt.");
        Argument<string?> deleteId = OptionalResourceArgument("id", "prompt GUID or name");
        delete.Add(deleteId);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Delete(pr.GetValue(deleteId), ct).ConfigureAwait(false));
        prompt.Add(delete);

        Command render = new("render", "Render a prompt template with parameters.");
        Argument<string?> renderId = OptionalResourceArgument("id", "prompt GUID or name");
        Option<string[]> renderParam = new("--param") { AllowMultipleArgumentsPerToken = true, Description = "Template parameter as key=value; pass multiple times for several parameters." };
        render.Add(renderId); render.Add(renderParam);
        render.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Render(pr.GetValue(renderId), pr.GetValue(renderParam), ct).ConfigureAwait(false));
        prompt.Add(render);

        Command test = new("test", "Assemble the system prompt without LLM cost.");
        Argument<string?> testId = OptionalResourceArgument("id", "prompt GUID or name");
        test.Add(testId);
        test.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Test(pr.GetValue(testId), ct).ConfigureAwait(false));
        prompt.Add(test);

        Command execute = new("execute", "Render and run session-backed inference.");
        Argument<string?> executeId = OptionalResourceArgument("id", "prompt GUID or name");
        Option<string?> executeInput = new("--input") { Description = "User message for the prompt turn: inline text, or @filename to read from a file." };
        Option<string[]> executeParam = new("--param") { AllowMultipleArgumentsPerToken = true, Description = "Template parameter as key=value; pass multiple times for several parameters." };
        Option<string?> executeSessionId = new("--session-id") { Description = "Session GUID to bind context from." };
        execute.Add(executeId); execute.Add(executeInput); execute.Add(executeParam); execute.Add(executeSessionId);
        execute.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Execute(
                pr.GetValue(executeId),
                pr.GetValue(executeInput),
                pr.GetValue(executeParam),
                ActiveSession(sp, pr.GetValue(executeSessionId)),
                ct).ConfigureAwait(false));
        prompt.Add(execute);

        Command clone = new("clone", "Clone a prompt to a new name/version.");
        Argument<string?> cloneId = OptionalResourceArgument("id", "prompt GUID or name");
        Option<string?> cloneNewName = new("--new-name") { Description = "New prompt name." };
        Option<string?> cloneNewVersion = new("--new-version") { Description = "New prompt version label." };
        Option<string?> cloneCampaign = new("--campaign") { Description = "Campaign GUID to associate the clone with." };
        clone.Add(cloneId); clone.Add(cloneNewName); clone.Add(cloneNewVersion); clone.Add(cloneCampaign);
        clone.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Clone(
                pr.GetValue(cloneId),
                pr.GetValue(cloneNewName),
                pr.GetValue(cloneNewVersion),
                ActiveCampaign(sp, pr.GetValue(cloneCampaign)),
                ct).ConfigureAwait(false));
        prompt.Add(clone);

        Command export = new("export", "Export a prompt as portable JSON.");
        Argument<string?> exportId = OptionalResourceArgument("id", "prompt GUID or name");
        Option<string?> exportOutput = new("--output") { Description = "Write exported JSON to this file instead of stdout." };
        export.Add(exportId); export.Add(exportOutput);
        export.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Export(pr.GetValue(exportId), pr.GetValue(exportOutput), ct).ConfigureAwait(false));
        prompt.Add(export);

        Command import = new("import", "Import a prompt from portable JSON.");
        Option<string?> importFile = new("--file") { Description = "Path to a prompt export JSON file." };
        Option<string?> importCampaignId = new("--campaign-id") { Description = "Campaign GUID to associate the import with." };
        import.Add(importFile); import.Add(importCampaignId);
        import.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Import(pr.GetValue(importFile), ActiveCampaign(sp, pr.GetValue(importCampaignId)), ct).ConfigureAwait(false));
        prompt.Add(import);

        return prompt;
    }

}
