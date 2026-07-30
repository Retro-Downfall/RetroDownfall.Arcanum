using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildSpell(IServiceProvider sp)
    {
        SpellCommands handler = sp.GetRequiredService<SpellCommands>();
        Command spell = new("spell", "Spell utilities (requires arcanum serve).");

        Command list = new("list", "List spells.");
        Option<string?> listWorkspace = new("--workspace");
        list.Add(listWorkspace);
        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(pr.GetValue(listWorkspace), ct).ConfigureAwait(false));
        spell.Add(list);

        Command get = new("get", "Show spell detail.");
        Argument<string> getName = new("name");
        Option<string?> getWorkspace = new("--workspace");
        get.Add(getName); get.Add(getWorkspace);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(getName)!, pr.GetValue(getWorkspace), ct).ConfigureAwait(false));
        spell.Add(get);

        Command create = new("create", "Create a spell.");
        Option<string?> createName = new("--name");
        Option<string?> createWorkspace = new("--workspace");
        Option<string?> createDescription = new("--description");
        Option<string?> createBody = new("--body");
        Option<string[]> createTag = new("--tag") { AllowMultipleArgumentsPerToken = true };
        Option<string[]> createDeclaredTool = new("--declared-tool") { AllowMultipleArgumentsPerToken = true };
        Option<string[]> createDependency = new("--dependency") { AllowMultipleArgumentsPerToken = true };
        create.Add(createName); create.Add(createWorkspace); create.Add(createDescription);
        create.Add(createBody); create.Add(createTag); create.Add(createDeclaredTool);
        create.Add(createDependency);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createName),
                pr.GetValue(createWorkspace),
                pr.GetValue(createDescription),
                pr.GetValue(createBody),
                pr.GetValue(createTag),
                pr.GetValue(createDeclaredTool),
                pr.GetValue(createDependency),
                ct).ConfigureAwait(false));
        spell.Add(create);

        Command update = new("update", "Update a spell.");
        Argument<string> updateName = new("name");
        Option<string?> updateWorkspace = new("--workspace");
        Option<string?> updateDescription = new("--description");
        Option<string[]> updateTag = new("--tag") { AllowMultipleArgumentsPerToken = true };
        update.Add(updateName); update.Add(updateWorkspace); update.Add(updateDescription); update.Add(updateTag);
        update.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Update(
                pr.GetValue(updateName)!,
                pr.GetValue(updateWorkspace),
                pr.GetValue(updateDescription),
                pr.GetValue(updateTag),
                ct).ConfigureAwait(false));
        spell.Add(update);

        Command delete = new("delete", "Delete a spell.");
        Argument<string> deleteName = new("name");
        Option<string?> deleteWorkspace = new("--workspace");
        delete.Add(deleteName); delete.Add(deleteWorkspace);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Delete(pr.GetValue(deleteName)!, pr.GetValue(deleteWorkspace), ct).ConfigureAwait(false));
        spell.Add(delete);

        Command search = new("search", "Search spells by query, tag, tool, or source.");
        Option<string?> searchQuery = new("--query", "-q");
        Option<string?> searchTag = new("--tag");
        Option<string?> searchTool = new("--tool");
        Option<string?> searchSource = new("--source");
        Option<string?> searchWorkspace = new("--workspace");
        search.Add(searchQuery); search.Add(searchTag); search.Add(searchTool);
        search.Add(searchSource); search.Add(searchWorkspace);
        search.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Search(
                pr.GetValue(searchQuery),
                pr.GetValue(searchTag),
                pr.GetValue(searchTool),
                pr.GetValue(searchSource),
                pr.GetValue(searchWorkspace),
                ct).ConfigureAwait(false));
        spell.Add(search);

        Command validate = new("validate", "Validate a spell's frontmatter and dependencies.");
        Argument<string> validateName = new("name");
        Option<string?> validateWorkspace = new("--workspace");
        validate.Add(validateName); validate.Add(validateWorkspace);
        validate.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Validate(pr.GetValue(validateName)!, pr.GetValue(validateWorkspace), ct).ConfigureAwait(false));
        spell.Add(validate);

        Command execute = new("execute", "Execute a spell and print the assistant response.");
        Argument<string> executeName = new("name");
        Option<string?> executeWorkspace = new("--workspace");
        Option<string?> executeVersion = new("--version");
        Option<string?> executeInput = new("--input");
        execute.Add(executeName); execute.Add(executeWorkspace); execute.Add(executeVersion); execute.Add(executeInput);
        execute.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Execute(
                pr.GetValue(executeName)!,
                pr.GetValue(executeWorkspace),
                pr.GetValue(executeVersion),
                pr.GetValue(executeInput),
                ct).ConfigureAwait(false));
        spell.Add(execute);

        Command versions = new("versions", "List spell versions.");
        Argument<string> versionsName = new("name");
        Option<string?> versionsWorkspace = new("--workspace");
        versions.Add(versionsName); versions.Add(versionsWorkspace);
        versions.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Versions(pr.GetValue(versionsName)!, pr.GetValue(versionsWorkspace), ct).ConfigureAwait(false));
        spell.Add(versions);

        Command export = new("export", "Export a spell as portable JSON.");
        Argument<string> exportName = new("name");
        Option<string?> exportWorkspace = new("--workspace");
        Option<string?> exportOutput = new("--output");
        export.Add(exportName); export.Add(exportWorkspace); export.Add(exportOutput);
        export.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Export(pr.GetValue(exportName)!, pr.GetValue(exportWorkspace), pr.GetValue(exportOutput), ct).ConfigureAwait(false));
        spell.Add(export);

        Command import = new("import", "Import a spell from portable JSON.");
        Option<string?> importFile = new("--file");
        Option<string?> importWorkspace = new("--workspace");
        import.Add(importFile); import.Add(importWorkspace);
        import.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Import(pr.GetValue(importFile), pr.GetValue(importWorkspace), ct).ConfigureAwait(false));
        spell.Add(import);

        Command cast = new("cast", "Dry-run preview of a spell's assembled system prompt.");
        Argument<string> castName = new("name");
        Option<string?> castWorkspace = new("--workspace");
        Option<string?> castSession = new("--session");
        Option<string?> castCampaign = new("--campaign");
        cast.Add(castName); cast.Add(castWorkspace); cast.Add(castSession); cast.Add(castCampaign);
        cast.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Cast(
                pr.GetValue(castName)!,
                pr.GetValue(castWorkspace),
                pr.GetValue(castSession),
                pr.GetValue(castCampaign),
                ct).ConfigureAwait(false));
        spell.Add(cast);

        Command clone = new("clone", "Clone a spell to a new name.");
        Argument<string> cloneName = new("name");
        Option<string?> cloneNewName = new("--new-name");
        Option<string?> cloneWorkspace = new("--workspace");
        clone.Add(cloneName); clone.Add(cloneNewName); clone.Add(cloneWorkspace);
        clone.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Clone(pr.GetValue(cloneName)!, pr.GetValue(cloneNewName), pr.GetValue(cloneWorkspace), ct).ConfigureAwait(false));
        spell.Add(clone);

        return spell;
    }

    private static Command BuildSpellVersion(IServiceProvider sp)
    {
        SpellVersionCommands handler = sp.GetRequiredService<SpellVersionCommands>();
        Command version = new("version", "Manage named spell file versions.");

        Command create = new("create", "Create a new spell version.");
        Argument<string> createName = new("name");
        Option<string?> createVersion = new("--version");
        Option<string?> createBody = new("--body");
        Option<string?> createWorkspace = new("--workspace");
        create.Add(createName); create.Add(createVersion); create.Add(createBody); create.Add(createWorkspace);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createName)!,
                pr.GetValue(createVersion),
                pr.GetValue(createBody),
                pr.GetValue(createWorkspace),
                ct).ConfigureAwait(false));
        version.Add(create);

        Command update = new("update", "Update an existing spell version's body.");
        Argument<string> updateName = new("name");
        Option<string?> updateVersion = new("--version");
        Option<string?> updateBody = new("--body");
        Option<string?> updateWorkspace = new("--workspace");
        update.Add(updateName); update.Add(updateVersion); update.Add(updateBody); update.Add(updateWorkspace);
        update.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Update(
                pr.GetValue(updateName)!,
                pr.GetValue(updateVersion),
                pr.GetValue(updateBody),
                pr.GetValue(updateWorkspace),
                ct).ConfigureAwait(false));
        version.Add(update);

        Command activate = new("activate", "Activate a spell version, swapping it into SPELL.md.");
        Argument<string> activateName = new("name");
        Option<string?> activateVersion = new("--version");
        Option<string?> activateWorkspace = new("--workspace");
        activate.Add(activateName); activate.Add(activateVersion); activate.Add(activateWorkspace);
        activate.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Activate(
                pr.GetValue(activateName)!,
                pr.GetValue(activateVersion),
                pr.GetValue(activateWorkspace),
                ct).ConfigureAwait(false));
        version.Add(activate);

        return version;
    }

    private static Command BuildCampaign(IServiceProvider sp)
    {
        CampaignCommands handler = sp.GetRequiredService<CampaignCommands>();
        CampaignCodexCommands codexHandler = sp.GetRequiredService<CampaignCodexCommands>();
        Command campaign = new("campaign", "Campaign registry (requires arcanum serve).");

        Command list = new("list", "List registered campaigns.");
        Option<string?> listType = new("--type");
        list.Add(listType);
        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(pr.GetValue(listType), ct).ConfigureAwait(false));
        campaign.Add(list);

        Command get = new("get", "Show campaign detail.");
        Argument<string> getId = new("id");
        get.Add(getId);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(getId)!, ct).ConfigureAwait(false));
        campaign.Add(get);

        Command create = new("create", "Register a new campaign.");
        Option<string?> createName = new("--name");
        Option<string?> createPath = new("--path");
        Option<string?> createType = new("--type");
        Option<string?> createDescription = new("--description");
        create.Add(createName); create.Add(createPath); create.Add(createType); create.Add(createDescription);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createName),
                pr.GetValue(createPath),
                pr.GetValue(createType),
                pr.GetValue(createDescription),
                ct).ConfigureAwait(false));
        campaign.Add(create);

        Command update = new("update", "Update a campaign.");
        Argument<string> updateId = new("id");
        Option<string?> updateName = new("--name");
        update.Add(updateId); update.Add(updateName);
        update.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Update(pr.GetValue(updateId)!, pr.GetValue(updateName), ct).ConfigureAwait(false));
        campaign.Add(update);

        Command delete = new("delete", "Remove a campaign.");
        Argument<string> deleteId = new("id");
        delete.Add(deleteId);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Delete(pr.GetValue(deleteId)!, ct).ConfigureAwait(false));
        campaign.Add(delete);

        Command export = new("export", "Export a campaign's spells and prompts as JSON.");
        Argument<string> exportId = new("id");
        Option<string?> exportOutput = new("--output");
        export.Add(exportId); export.Add(exportOutput);
        export.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Export(pr.GetValue(exportId)!, pr.GetValue(exportOutput), ct).ConfigureAwait(false));
        campaign.Add(export);

        Command import = new("import", "Import spells and prompts into a campaign.");
        Argument<string> importId = new("id");
        Option<string?> importFile = new("--file");
        import.Add(importId); import.Add(importFile);
        import.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Import(pr.GetValue(importId)!, pr.GetValue(importFile), ct).ConfigureAwait(false));
        campaign.Add(import);

        Command spells = new("spells", "List spells scoped to a campaign, shadowing built-ins.");
        Argument<string> spellsId = new("id");
        Option<string?> spellsQuery = new("--query", "-q");
        Option<string?> spellsTag = new("--tag");
        Option<string?> spellsTool = new("--tool");
        spells.Add(spellsId); spells.Add(spellsQuery); spells.Add(spellsTag); spells.Add(spellsTool);
        spells.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Spells(
                pr.GetValue(spellsId)!,
                pr.GetValue(spellsQuery),
                pr.GetValue(spellsTag),
                pr.GetValue(spellsTool),
                ct).ConfigureAwait(false));
        campaign.Add(spells);

        Command prompts = new("prompts", "List prompts scoped to a campaign.");
        Argument<string> promptsId = new("id");
        Option<string?> promptsQuery = new("--query", "-q");
        Option<string?> promptsTag = new("--tag");
        prompts.Add(promptsId); prompts.Add(promptsQuery); prompts.Add(promptsTag);
        prompts.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Prompts(pr.GetValue(promptsId)!, pr.GetValue(promptsQuery), pr.GetValue(promptsTag), ct).ConfigureAwait(false));
        campaign.Add(prompts);

        Command sessions = new("sessions", "List sessions scoped to a campaign.");
        Argument<string> sessionsId = new("id");
        Option<string?> sessionsStatus = new("--status");
        Option<string?> sessionsSearch = new("--search");
        Option<int?> sessionsLimit = new("--limit");
        Option<string?> sessionsBeforeUpdatedAt = new("--before-updated-at");
        sessions.Add(sessionsId); sessions.Add(sessionsStatus); sessions.Add(sessionsSearch);
        sessions.Add(sessionsLimit); sessions.Add(sessionsBeforeUpdatedAt);
        sessions.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Sessions(
                pr.GetValue(sessionsId)!,
                pr.GetValue(sessionsStatus),
                pr.GetValue(sessionsSearch),
                pr.GetValue(sessionsLimit),
                pr.GetValue(sessionsBeforeUpdatedAt),
                ct).ConfigureAwait(false));
        campaign.Add(sessions);

        Command codex = new("codex", "Manage the campaign's CODEX.md scratchpad.");

        Command codexGet = new("get", "Print CODEX.md.");
        Argument<string> codexGetId = new("id");
        codexGet.Add(codexGetId);
        codexGet.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await codexHandler.Get(pr.GetValue(codexGetId)!, ct).ConfigureAwait(false));
        codex.Add(codexGet);

        Command codexPut = new("put", "Write CODEX.md from a file.");
        Argument<string> codexPutId = new("id");
        Option<string?> codexPutFile = new("--file");
        codexPut.Add(codexPutId); codexPut.Add(codexPutFile);
        codexPut.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await codexHandler.Put(pr.GetValue(codexPutId)!, pr.GetValue(codexPutFile), ct).ConfigureAwait(false));
        codex.Add(codexPut);

        Command codexDelete = new("delete", "Delete CODEX.md.");
        Argument<string> codexDeleteId = new("id");
        codexDelete.Add(codexDeleteId);
        codexDelete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await codexHandler.Delete(pr.GetValue(codexDeleteId)!, ct).ConfigureAwait(false));
        codex.Add(codexDelete);

        campaign.Add(codex);

        return campaign;
    }

    private static Command BuildSession(IServiceProvider sp)
    {
        SessionCommands handler = sp.GetRequiredService<SessionCommands>();
        Command session = new("session", "Session semantic search (requires arcanum serve).");

        Command divine = new("divine", "Semantic search over Grimoire entries.");
        Argument<string> divineQuery = new("query");
        Option<int?> divineLimit = new("--limit");
        Option<string?> divineCampaign = new("--campaign");
        Option<string?> divineStatus = new("--status");
        divine.Add(divineQuery); divine.Add(divineLimit); divine.Add(divineCampaign); divine.Add(divineStatus);
        divine.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Divine(
                pr.GetValue(divineQuery)!,
                pr.GetValue(divineLimit),
                pr.GetValue(divineCampaign),
                pr.GetValue(divineStatus),
                ct).ConfigureAwait(false));
        session.Add(divine);

        return session;
    }
}
