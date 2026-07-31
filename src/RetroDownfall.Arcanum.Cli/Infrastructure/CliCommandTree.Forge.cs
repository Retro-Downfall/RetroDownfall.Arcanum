using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;
using RetroDownfall.Arcanum.Cli.Commands.Wards;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildPrompt(IServiceProvider sp)
    {
        PromptCommands handler = sp.GetRequiredService<PromptCommands>();
        Command prompt = new("prompt", "The Forge prompt utilities (requires arcanum serve).");

        Command list = new("list", "List prompts.");
        Option<string?> listCampaignId = new("--campaign-id", "--campaignId") { Description = "Filter by campaign GUID." };
        Option<string?> listQuery = new("--query", "-q") { Description = "Free-text query." };
        Option<string?> listTag = new("--tag") { Description = "Filter by tag." };
        list.Add(listCampaignId); list.Add(listQuery); list.Add(listTag);
        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(pr.GetValue(listCampaignId), pr.GetValue(listQuery), pr.GetValue(listTag), ct).ConfigureAwait(false));
        prompt.Add(list);

        Command get = new("get", "Show prompt detail.");
        Argument<string?> getId = new("id")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Optional prompt GUID, exact name, or unique name prefix; omit for an interactive picker.",
        };
        get.Add(getId);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(getId), ct).ConfigureAwait(false));
        prompt.Add(get);

        Command versions = new("versions", "List versions of a prompt by name.");
        Argument<string> versionsName = new("name") { Description = "Prompt name." };
        Option<string?> versionsCampaignId = new("--campaign-id", "--campaignId") { Description = "Filter by campaign GUID." };
        versions.Add(versionsName); versions.Add(versionsCampaignId);
        versions.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Versions(pr.GetValue(versionsName)!, pr.GetValue(versionsCampaignId), ct).ConfigureAwait(false));
        prompt.Add(versions);

        Command create = new("create", "Create a prompt.");
        Option<string?> createName = new("--name") { Description = "Prompt name." };
        Option<string?> createVersion = new("--version") { Description = "Prompt version label." };
        Option<string?> createTemplate = new("--template") { Description = "Prompt template: inline text, or @filename to read from a file." };
        Option<string?> createCampaignId = new("--campaign-id", "--campaignId") { Description = "Campaign GUID to associate with." };
        Option<string?> createDescription = new("--description") { Description = "Prompt description." };
        Option<string[]> createTag = new("--tag") { AllowMultipleArgumentsPerToken = true, Description = "Tag; pass multiple times for several tags." };
        create.Add(createName); create.Add(createVersion); create.Add(createTemplate);
        create.Add(createCampaignId); create.Add(createDescription); create.Add(createTag);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createName),
                pr.GetValue(createVersion),
                pr.GetValue(createTemplate),
                pr.GetValue(createCampaignId),
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
        Option<string?> executeSessionId = new("--session-id", "--sessionId") { Description = "Session GUID to bind context from." };
        execute.Add(executeId); execute.Add(executeInput); execute.Add(executeParam); execute.Add(executeSessionId);
        execute.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Execute(
                pr.GetValue(executeId),
                pr.GetValue(executeInput),
                pr.GetValue(executeParam),
                pr.GetValue(executeSessionId),
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
                pr.GetValue(cloneCampaign),
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
        Option<string?> importCampaignId = new("--campaign-id", "--campaignId") { Description = "Campaign GUID to associate the import with." };
        import.Add(importFile); import.Add(importCampaignId);
        import.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Import(pr.GetValue(importFile), pr.GetValue(importCampaignId), ct).ConfigureAwait(false));
        prompt.Add(import);

        return prompt;
    }

    private static Command BuildApprentice(IServiceProvider sp)
    {
        ApprenticeCommands handler = sp.GetRequiredService<ApprenticeCommands>();
        Command apprentice = new("apprentice", "The Forge Apprentice orchestration (requires arcanum serve).");

        Command list = new("list", "List Apprentices.");
        Option<string?> listCampaignId = new("--campaign-id", "--campaignId") { Description = "Filter by campaign GUID." };
        Option<string?> listStatus = new("--status") { Description = "Filter by status." };
        Option<int?> listLimit = new("--limit") { Description = "Maximum number of Apprentices to return." };
        list.Add(listCampaignId); list.Add(listStatus); list.Add(listLimit);
        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(pr.GetValue(listCampaignId), pr.GetValue(listStatus), pr.GetValue(listLimit), ct).ConfigureAwait(false));
        apprentice.Add(list);

        Command get = new("get", "Show Apprentice detail.");
        Argument<string?> getId = new("id")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Optional Apprentice GUID, exact name, or unique name prefix; omit for an interactive picker.",
        };
        get.Add(getId);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(getId), ct).ConfigureAwait(false));
        apprentice.Add(get);

        Command create = new("create", "Create an Apprentice.");
        Option<string?> createGoal = new("--goal") { Description = "Apprentice goal: inline text, or @filename to read from a file." };
        Option<string?> createName = new("--name") { Description = "Display name; defaults to a truncated form of the goal." };
        Option<string?> createCampaignId = new("--campaign-id", "--campaignId") { Description = "Campaign GUID to associate with." };
        Option<string?> createWorkspace = new("--workspace") { Description = "Workspace root to scope the Apprentice." };
        create.Add(createGoal); create.Add(createName); create.Add(createCampaignId); create.Add(createWorkspace);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createGoal),
                pr.GetValue(createName),
                pr.GetValue(createCampaignId),
                pr.GetValue(createWorkspace),
                ct).ConfigureAwait(false));
        apprentice.Add(create);

        Command delete = new("delete", "Delete a terminal Apprentice.");
        Argument<string?> deleteId = OptionalResourceArgument("id", "Apprentice GUID or name");
        delete.Add(deleteId);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Delete(pr.GetValue(deleteId), ct).ConfigureAwait(false));
        apprentice.Add(delete);

        Command start = new("start", "Start plan generation and execution.");
        Argument<string?> startId = OptionalResourceArgument("id", "Apprentice GUID or name");
        start.Add(startId);
        start.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Start(pr.GetValue(startId), ct).ConfigureAwait(false));
        apprentice.Add(start);

        Command pause = new("pause", "Pause at the next step boundary.");
        Argument<string?> pauseId = OptionalResourceArgument("id", "Apprentice GUID or name");
        pause.Add(pauseId);
        pause.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Pause(pr.GetValue(pauseId), ct).ConfigureAwait(false));
        apprentice.Add(pause);

        Command resume = new("resume", "Resume from checkpoint.");
        Argument<string?> resumeId = OptionalResourceArgument("id", "Apprentice GUID or name");
        resume.Add(resumeId);
        resume.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Resume(pr.GetValue(resumeId), ct).ConfigureAwait(false));
        apprentice.Add(resume);

        Command cancel = new("cancel", "Cancel execution.");
        Argument<string?> cancelId = OptionalResourceArgument("id", "Apprentice GUID or name");
        cancel.Add(cancelId);
        cancel.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Cancel(pr.GetValue(cancelId), ct).ConfigureAwait(false));
        apprentice.Add(cancel);

        Command reweave = new("reweave", "Replace the remaining plan steps.");
        Argument<string?> reweaveId = OptionalResourceArgument("id", "Apprentice GUID or name");
        Option<string?> reweavePlan = new("--plan") { Description = "JSON array of plan steps: inline text, or @filename to read from a file." };
        reweave.Add(reweaveId); reweave.Add(reweavePlan);
        reweave.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Reweave(pr.GetValue(reweaveId), pr.GetValue(reweavePlan), ct).ConfigureAwait(false));
        apprentice.Add(reweave);

        Command intervene = new("intervene", "Provide Divine Intervention guidance to an escalated Apprentice.");
        Argument<string?> interveneId = OptionalResourceArgument("id", "Apprentice GUID or name");
        Option<string?> interveneGuidance = new("--guidance") { Description = "Guidance text for the escalated Apprentice." };
        intervene.Add(interveneId); intervene.Add(interveneGuidance);
        intervene.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Intervene(pr.GetValue(interveneId), pr.GetValue(interveneGuidance), ct).ConfigureAwait(false));
        apprentice.Add(intervene);

        Command cast = new("cast", "Delegate a child Apprentice via The Conclave.");
        Argument<string?> castId = OptionalResourceArgument("id", "Apprentice GUID or name");
        Option<string?> castGoal = new("--goal") { Description = "Child Apprentice goal text." };
        Option<string?> castName = new("--name") { Description = "Display name for the child Apprentice." };
        cast.Add(castId); cast.Add(castGoal); cast.Add(castName);
        cast.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Cast(pr.GetValue(castId), pr.GetValue(castGoal), pr.GetValue(castName), ct).ConfigureAwait(false));
        apprentice.Add(cast);

        Command chronicle = new("chronicle", "Stream live Apprentice events (SSE).");
        Argument<string?> chronicleId = OptionalResourceArgument("id", "Apprentice GUID or name");
        chronicle.Add(chronicleId);
        chronicle.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Chronicle(pr.GetValue(chronicleId), ct).ConfigureAwait(false));
        apprentice.Add(chronicle);

        return apprentice;
    }

    private static Command BuildWard(IServiceProvider sp)
    {
        WardCommands handler = sp.GetRequiredService<WardCommands>();
        Command ward = new("ward", "Ward approval gates for Forbidden Arts (requires arcanum serve).");

        Command list = new("list", "List active wards.");
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(ct).ConfigureAwait(false));
        ward.Add(list);

        Command get = new("get", "Show ward detail.");
        Argument<string> getId = new("id") { Description = "Ward ID." };
        get.Add(getId);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(getId)!, ct).ConfigureAwait(false));
        ward.Add(get);

        Command resolve = new("resolve", "Allow or deny a ward.");
        Argument<string> resolveId = new("id") { Description = "Ward ID." };
        Option<bool> resolveAllow = new("--allow") { Description = "Allow the warded tool call to proceed." };
        Option<bool> resolveDeny = new("--deny") { Description = "Deny the warded tool call." };
        Option<string?> resolveReason = new("--reason") { Description = "Optional reason recorded with the resolution." };
        resolve.Add(resolveId); resolve.Add(resolveAllow); resolve.Add(resolveDeny); resolve.Add(resolveReason);
        resolve.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Resolve(
                pr.GetValue(resolveId)!,
                pr.GetValue(resolveAllow),
                pr.GetValue(resolveDeny),
                pr.GetValue(resolveReason),
                ct).ConfigureAwait(false));
        ward.Add(resolve);

        return ward;
    }

    private static Command BuildTrial(IServiceProvider sp)
    {
        TrialCommands handler = sp.GetRequiredService<TrialCommands>();
        Command trial = new("trial", "Run Trials against spells, prompts, or Apprentice goals (requires arcanum serve).");

        Command run = new("run", "Run a Trial with Inquisitors.");
        Option<string?> runTarget = new("--target") { Description = "Trial target kind: spell, prompt, apprenticeGoal." };
        Option<string?> runTargetValue = new("--target-value") { Description = "Spell name, prompt GUID, or apprentice goal text." };
        Option<string?> runModel = new("--model") { Description = "Model override for the Trial." };
        Option<string?> runWorkspace = new("--workspace") { Description = "Workspace root to scope the Trial." };
        Option<string?> runName = new("--name") { Description = "Trial display name; defaults to '{targetKind}:{target}'." };
        Option<string[]> runInquisitor = new("--inquisitor") { AllowMultipleArgumentsPerToken = true, Description = "Inquisitor spec: inline JSON, or @filename. Pass multiple times for several inquisitors." };
        Option<string[]> runVar = new("--var") { AllowMultipleArgumentsPerToken = true, Description = "Trial variable as key=value; pass multiple times for several variables." };
        run.Add(runTarget); run.Add(runTargetValue); run.Add(runModel); run.Add(runWorkspace);
        run.Add(runName); run.Add(runInquisitor); run.Add(runVar);
        run.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Run(
                pr.GetValue(runTarget),
                pr.GetValue(runTargetValue),
                pr.GetValue(runModel),
                pr.GetValue(runWorkspace),
                pr.GetValue(runName),
                pr.GetValue(runInquisitor),
                pr.GetValue(runVar),
                ct).ConfigureAwait(false));
        trial.Add(run);

        return trial;
    }
}
