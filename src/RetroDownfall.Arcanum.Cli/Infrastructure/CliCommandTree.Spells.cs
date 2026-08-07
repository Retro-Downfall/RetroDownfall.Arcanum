using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildSpell(IServiceProvider sp)
    {
        SpellCommands handler = sp.GetRequiredService<SpellCommands>();
        Command spell = new("spell", "Spell utilities (requires arcanum serve).");

        Command list = new("list", "List spells.");
        Option<string?> listWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        list.Add(listWorkspace);
        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(ActiveWorkspace(sp, pr.GetValue(listWorkspace)), ct).ConfigureAwait(false));
        spell.Add(list);

        Command get = new("get", "Show spell detail.");
        Argument<string?> getName = new("name")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Optional exact spell name or unique name prefix; omit for an interactive picker.",
        };
        Option<string?> getWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        get.Add(getName); get.Add(getWorkspace);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(getName), ActiveWorkspace(sp, pr.GetValue(getWorkspace)), ct).ConfigureAwait(false));
        spell.Add(get);

        Command create = new("create", "Create a spell.");
        Option<string?> createName = new("--name") { Description = "Spell name." };
        Option<string?> createWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        Option<string?> createDescription = new("--description") { Description = "Short spell description stored in the frontmatter." };
        Option<string?> createBody = new("--body") { Description = "Spell body Markdown." };
        Option<string[]> createTag = new("--tag") { AllowMultipleArgumentsPerToken = true, Description = "Tag to attach; repeatable." };
        Option<string[]> createDeclaredTool = new("--declared-tool") { AllowMultipleArgumentsPerToken = true, Description = "Tool the spell declares it may use; repeatable." };
        Option<string[]> createDependency = new("--dependency") { AllowMultipleArgumentsPerToken = true, Description = "Spell dependency by name; repeatable." };
        create.Add(createName); create.Add(createWorkspace); create.Add(createDescription);
        create.Add(createBody); create.Add(createTag); create.Add(createDeclaredTool);
        create.Add(createDependency);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createName),
                ActiveWorkspace(sp, pr.GetValue(createWorkspace)),
                pr.GetValue(createDescription),
                pr.GetValue(createBody),
                pr.GetValue(createTag),
                pr.GetValue(createDeclaredTool),
                pr.GetValue(createDependency),
                ct).ConfigureAwait(false));
        spell.Add(create);

        Command update = new("update", "Update a spell.");
        Argument<string> updateName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> updateWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        Option<string?> updateDescription = new("--description") { Description = "Replacement spell description." };
        Option<string[]> updateTag = new("--tag") { AllowMultipleArgumentsPerToken = true, Description = "Replacement tag; repeatable." };
        update.Add(updateName); update.Add(updateWorkspace); update.Add(updateDescription); update.Add(updateTag);
        update.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Update(
                pr.GetValue(updateName)!,
                ActiveWorkspace(sp, pr.GetValue(updateWorkspace)),
                pr.GetValue(updateDescription),
                pr.GetValue(updateTag),
                ct).ConfigureAwait(false));
        spell.Add(update);

        Command delete = new("delete", "Delete a spell.");
        Argument<string> deleteName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> deleteWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        delete.Add(deleteName); delete.Add(deleteWorkspace);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Delete(pr.GetValue(deleteName)!, ActiveWorkspace(sp, pr.GetValue(deleteWorkspace)), ct).ConfigureAwait(false));
        spell.Add(delete);

        Command search = new("search", "Search spells by query, tag, tool, or source.");
        Option<string?> searchQuery = new("--query", "-q") { Description = "Free-text query matched against spell name, description, and body." };
        Option<string?> searchTag = new("--tag") { Description = "Restrict results to spells carrying this tag." };
        Option<string?> searchTool = new("--tool") { Description = "Restrict results to spells declaring this tool." };
        Option<string?> searchSource = new("--source") { Description = "Restrict results to this spell source." };
        Option<string?> searchWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        search.Add(searchQuery); search.Add(searchTag); search.Add(searchTool);
        search.Add(searchSource); search.Add(searchWorkspace);
        search.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Search(
                pr.GetValue(searchQuery),
                pr.GetValue(searchTag),
                pr.GetValue(searchTool),
                pr.GetValue(searchSource),
                ActiveWorkspace(sp, pr.GetValue(searchWorkspace)),
                ct).ConfigureAwait(false));
        spell.Add(search);

        Command validate = new("validate", "Validate a spell's frontmatter and dependencies.");
        Argument<string> validateName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> validateWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        validate.Add(validateName); validate.Add(validateWorkspace);
        validate.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Validate(pr.GetValue(validateName)!, ActiveWorkspace(sp, pr.GetValue(validateWorkspace)), ct).ConfigureAwait(false));
        spell.Add(validate);

        Command execute = new("execute", "Execute a spell and print the assistant response.");
        Argument<string> executeName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> executeWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        Option<string?> executeVersion = new("--version") { Description = "Named spell version to execute; defaults to the active version." };
        Option<string?> executeInput = new("--input") { Description = "Input text passed to the spell." };
        execute.Add(executeName); execute.Add(executeWorkspace); execute.Add(executeVersion); execute.Add(executeInput);
        execute.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Execute(
                pr.GetValue(executeName)!,
                ActiveWorkspace(sp, pr.GetValue(executeWorkspace)),
                pr.GetValue(executeVersion),
                pr.GetValue(executeInput),
                ct).ConfigureAwait(false));
        spell.Add(execute);

        Command versions = new("versions", "List spell versions.");
        Argument<string> versionsName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> versionsWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        versions.Add(versionsName); versions.Add(versionsWorkspace);
        versions.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Versions(pr.GetValue(versionsName)!, ActiveWorkspace(sp, pr.GetValue(versionsWorkspace)), ct).ConfigureAwait(false));
        spell.Add(versions);

        Command export = new("export", "Export a spell as portable JSON.");
        Argument<string> exportName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> exportWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        Option<string?> exportOutput = new("--output") { Description = "Destination file path; omit to write the JSON to stdout." };
        export.Add(exportName); export.Add(exportWorkspace); export.Add(exportOutput);
        export.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Export(pr.GetValue(exportName)!, ActiveWorkspace(sp, pr.GetValue(exportWorkspace)), pr.GetValue(exportOutput), ct).ConfigureAwait(false));
        spell.Add(export);

        Command import = new("import", "Import a spell from portable JSON.");
        Option<string?> importFile = new("--file") { Description = "Portable spell JSON file to import." };
        Option<string?> importWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        import.Add(importFile); import.Add(importWorkspace);
        import.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Import(pr.GetValue(importFile), ActiveWorkspace(sp, pr.GetValue(importWorkspace)), ct).ConfigureAwait(false));
        spell.Add(import);

        Command cast = new("cast", "Dry-run preview of a spell's assembled system prompt.");
        Argument<string> castName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> castWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        Option<string?> castSession = new("--session") { Description = "Session GUID, exact title, or unique title prefix used for the preview." };
        Option<string?> castCampaign = new("--campaign") { Description = "Campaign GUID, exact name, or unique prefix used for the preview." };
        cast.Add(castName); cast.Add(castWorkspace); cast.Add(castSession); cast.Add(castCampaign);
        cast.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Cast(
                pr.GetValue(castName)!,
                ActiveWorkspace(sp, pr.GetValue(castWorkspace)),
                ActiveSession(sp, pr.GetValue(castSession)),
                ActiveCampaign(sp, pr.GetValue(castCampaign)),
                ct).ConfigureAwait(false));
        spell.Add(cast);

        Command clone = new("clone", "Clone a spell to a new name.");
        Argument<string> cloneName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> cloneNewName = new("--new-name") { Description = "Name for the cloned spell." };
        Option<string?> cloneWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        clone.Add(cloneName); clone.Add(cloneNewName); clone.Add(cloneWorkspace);
        clone.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Clone(pr.GetValue(cloneName)!, pr.GetValue(cloneNewName), ActiveWorkspace(sp, pr.GetValue(cloneWorkspace)), ct).ConfigureAwait(false));
        spell.Add(clone);

        return spell;
    }

    private static Command BuildSpellVersion(IServiceProvider sp)
    {
        SpellVersionCommands handler = sp.GetRequiredService<SpellVersionCommands>();
        Command version = new("version", "Manage named spell file versions.");

        Command create = new("create", "Create a new spell version.");
        Argument<string> createName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> createVersion = new("--version") { Description = "Version label to create." };
        Option<string?> createBody = new("--body") { Description = "Version body Markdown." };
        Option<string?> createWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        create.Add(createName); create.Add(createVersion); create.Add(createBody); create.Add(createWorkspace);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createName)!,
                pr.GetValue(createVersion),
                pr.GetValue(createBody),
                ActiveWorkspace(sp, pr.GetValue(createWorkspace)),
                ct).ConfigureAwait(false));
        version.Add(create);

        Command update = new("update", "Update an existing spell version's body.");
        Argument<string> updateName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> updateVersion = new("--version") { Description = "Version label to update." };
        Option<string?> updateBody = new("--body") { Description = "Replacement version body Markdown." };
        Option<string?> updateWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        update.Add(updateName); update.Add(updateVersion); update.Add(updateBody); update.Add(updateWorkspace);
        update.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Update(
                pr.GetValue(updateName)!,
                pr.GetValue(updateVersion),
                pr.GetValue(updateBody),
                ActiveWorkspace(sp, pr.GetValue(updateWorkspace)),
                ct).ConfigureAwait(false));
        version.Add(update);

        Command activate = new("activate", "Activate a spell version, swapping it into SPELL.md.");
        Argument<string> activateName = new("name") { Description = "Exact spell name or unique name prefix." };
        Option<string?> activateVersion = new("--version") { Description = "Version label to activate." };
        Option<string?> activateWorkspace = new("--workspace") { Description = "Workspace ID, name, or server-host path; defaults to the saved CLI context." };
        activate.Add(activateName); activate.Add(activateVersion); activate.Add(activateWorkspace);
        activate.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Activate(
                pr.GetValue(activateName)!,
                pr.GetValue(activateVersion),
                ActiveWorkspace(sp, pr.GetValue(activateWorkspace)),
                ct).ConfigureAwait(false));
        version.Add(activate);

        return version;
    }

    private static Command BuildCampaign(IServiceProvider sp)
    {
        CampaignCommands handler = sp.GetRequiredService<CampaignCommands>();
        CampaignCodexCommands codexHandler = sp.GetRequiredService<CampaignCodexCommands>();
        ContextCommands contextHandler = sp.GetRequiredService<ContextCommands>();

        Command campaign = new(
            "campaign",
            "Persistent project containers for sessions, spells, prompts, Codex, and Sanctum; filesystem access and indexing remain Workspace responsibilities.");

        Command list = new("list", "List registered campaigns.");
        Option<string?> listType = new("--type") { Description = "Restrict the listing to campaigns of this type." };
        list.Add(listType);
        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(pr.GetValue(listType), ct).ConfigureAwait(false));
        campaign.Add(list);

        Command get = new("get", "Show campaign detail.");
        Argument<string?> getId = new("id")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Optional campaign GUID, exact name, or unique name prefix; omit for an interactive picker.",
        };
        get.Add(getId);
        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(getId), ct).ConfigureAwait(false));
        campaign.Add(get);

        Command use = new(
            "use",
            "Select a Campaign in the shared persistent CLI context.");

        Argument<string> useIdentifier = new("campaign")
        {

            Description = "Campaign GUID or name.",

        };

        use.Add(useIdentifier);

        use.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await contextHandler.Use(
                CliContextScope.Campaign,
                pr.GetValue(useIdentifier)!,
                ct).ConfigureAwait(false));

        campaign.Add(use);

        Command create = new("create", "Register a new campaign.");
        Option<string?> createName = new("--name") { Description = "Campaign name." };
        Option<string?> createPath = new("--path") { Description = "Filesystem path to associate with the campaign." };
        Option<string?> createType = new("--type") { Description = "Campaign type." };
        Option<string?> createDescription = new("--description") { Description = "Short campaign description." };
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
        Argument<string?> updateId = OptionalResourceArgument("id", "campaign GUID or name");
        Option<string?> updateName = new("--name") { Description = "New campaign name." };
        update.Add(updateId); update.Add(updateName);
        update.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Update(pr.GetValue(updateId), pr.GetValue(updateName), ct).ConfigureAwait(false));
        campaign.Add(update);

        Command delete = new("delete", "Remove a campaign.");
        Argument<string?> deleteId = OptionalResourceArgument("id", "campaign GUID or name");
        delete.Add(deleteId);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Delete(pr.GetValue(deleteId), ct).ConfigureAwait(false));
        campaign.Add(delete);

        Command export = new("export", "Export a campaign's spells and prompts as JSON.");
        Argument<string?> exportId = OptionalResourceArgument("id", "campaign GUID or name");
        Option<string?> exportOutput = new("--output") { Description = "Destination file path; omit to write the JSON to stdout." };
        export.Add(exportId); export.Add(exportOutput);
        export.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Export(pr.GetValue(exportId), pr.GetValue(exportOutput), ct).ConfigureAwait(false));
        campaign.Add(export);

        Command import = new("import", "Import spells and prompts into a campaign.");
        Argument<string?> importId = OptionalResourceArgument("id", "campaign GUID or name");
        Option<string?> importFile = new("--file") { Description = "Portable campaign JSON file to import." };
        import.Add(importId); import.Add(importFile);
        import.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Import(pr.GetValue(importId), pr.GetValue(importFile), ct).ConfigureAwait(false));
        campaign.Add(import);

        Command spells = new("spells", "List spells scoped to a campaign, shadowing built-ins.");
        Argument<string?> spellsId = OptionalResourceArgument("id", "campaign GUID or name");
        Option<string?> spellsQuery = new("--query", "-q") { Description = "Free-text query matched against spell name, description, and body." };
        Option<string?> spellsTag = new("--tag") { Description = "Restrict results to spells carrying this tag." };
        Option<string?> spellsTool = new("--tool") { Description = "Restrict results to spells declaring this tool." };
        spells.Add(spellsId); spells.Add(spellsQuery); spells.Add(spellsTag); spells.Add(spellsTool);
        spells.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Spells(
                pr.GetValue(spellsId),
                pr.GetValue(spellsQuery),
                pr.GetValue(spellsTag),
                pr.GetValue(spellsTool),
                ct).ConfigureAwait(false));
        campaign.Add(spells);

        Command prompts = new("prompts", "List prompts scoped to a campaign.");
        Argument<string?> promptsId = OptionalResourceArgument("id", "campaign GUID or name");
        Option<string?> promptsQuery = new("--query", "-q") { Description = "Free-text query matched against prompt name, description, and body." };
        Option<string?> promptsTag = new("--tag") { Description = "Restrict results to prompts carrying this tag." };
        prompts.Add(promptsId); prompts.Add(promptsQuery); prompts.Add(promptsTag);
        prompts.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Prompts(pr.GetValue(promptsId), pr.GetValue(promptsQuery), pr.GetValue(promptsTag), ct).ConfigureAwait(false));
        campaign.Add(prompts);

        Command sessions = new("sessions", "List sessions scoped to a campaign.");
        Argument<string?> sessionsId = OptionalResourceArgument("id", "campaign GUID or name");
        Option<string?> sessionsStatus = new("--status") { Description = "Restrict the listing to sessions in this status." };
        Option<string?> sessionsSearch = new("--search") { Description = "Free-text search over session titles." };
        Option<int?> sessionsLimit = new("--limit") { Description = "Maximum number of sessions to return." };
        Option<string?> sessionsBeforeUpdatedAt = new("--before-updated-at") { Description = "Return only sessions last updated before this ISO-8601 timestamp." };
        sessions.Add(sessionsId); sessions.Add(sessionsStatus); sessions.Add(sessionsSearch);
        sessions.Add(sessionsLimit); sessions.Add(sessionsBeforeUpdatedAt);
        sessions.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Sessions(
                pr.GetValue(sessionsId),
                pr.GetValue(sessionsStatus),
                pr.GetValue(sessionsSearch),
                pr.GetValue(sessionsLimit),
                pr.GetValue(sessionsBeforeUpdatedAt),
                ct).ConfigureAwait(false));
        campaign.Add(sessions);

        Command codex = new("codex", "Manage the campaign's CODEX.md scratchpad.");

        Command codexGet = new("get", "Print CODEX.md.");
        Argument<string?> codexGetId = OptionalResourceArgument("id", "campaign GUID or name");
        codexGet.Add(codexGetId);
        codexGet.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await codexHandler.Get(pr.GetValue(codexGetId), ct).ConfigureAwait(false));
        codex.Add(codexGet);

        Command codexPut = new("put", "Write CODEX.md from a file.");
        Argument<string?> codexPutId = OptionalResourceArgument("id", "campaign GUID or name");
        Option<string?> codexPutFile = new("--file") { Description = "File whose contents replace CODEX.md." };
        codexPut.Add(codexPutId); codexPut.Add(codexPutFile);
        codexPut.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await codexHandler.Put(pr.GetValue(codexPutId), pr.GetValue(codexPutFile), ct).ConfigureAwait(false));
        codex.Add(codexPut);

        Command codexDelete = new("delete", "Delete CODEX.md.");
        Argument<string?> codexDeleteId = OptionalResourceArgument("id", "campaign GUID or name");
        codexDelete.Add(codexDeleteId);
        codexDelete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await codexHandler.Delete(pr.GetValue(codexDeleteId), ct).ConfigureAwait(false));
        codex.Add(codexDelete);

        campaign.Add(codex);

        return campaign;
    }

    private static Command BuildSession(IServiceProvider sp)
    {
        SessionCommands handler = sp.GetRequiredService<SessionCommands>();

        AttachmentCommands attachmentHandler = sp.GetRequiredService<AttachmentCommands>();

        Command session = new("session", "Manage and continue sessions through the Arcanum API.");

        Command list = new("list", "List recent sessions.");

        Option<string?> listCampaign = new("--campaign") { Description = "Filter by campaign GUID." };

        Option<string?> listStatus = new("--status") { Description = "Filter by session status." };

        Option<string?> listSearch = new("--search") { Description = "Filter by search text." };

        Option<string?> listModel = new("--model") { Description = "Filter by model." };

        Option<string?> listFrom = new("--from") { Description = "Include sessions on or after this ISO-8601 timestamp." };

        Option<string?> listTo = new("--to") { Description = "Include sessions on or before this ISO-8601 timestamp." };

        Option<int?> listLimit = new("--limit") { Description = "Maximum sessions per page." };

        list.Add(listCampaign);

        list.Add(listStatus);

        list.Add(listSearch);

        list.Add(listModel);

        list.Add(listFrom);

        list.Add(listTo);

        list.Add(listLimit);

        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(
                ActiveCampaign(sp, pr.GetValue(listCampaign)),
                pr.GetValue(listStatus),
                pr.GetValue(listSearch),
                pr.GetValue(listModel),
                pr.GetValue(listFrom),
                pr.GetValue(listTo),
                pr.GetValue(listLimit),
                ct).ConfigureAwait(false));

        session.Add(list);

        Command show = new("show", "Summarize a session, including telemetry and lineage.");

        Argument<string?> showIdentifier = SessionArgument();

        show.Add(showIdentifier);

        show.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Show(pr.GetValue(showIdentifier), ct).ConfigureAwait(false));

        session.Add(show);

        Command get = new("get", "Compatibility alias for session show.");

        Argument<string?> getIdentifier = SessionArgument();

        get.Add(getIdentifier);

        get.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(getIdentifier), ct).ConfigureAwait(false));

        session.Add(get);

        Command chat = new("chat", "Continue a session by GUID, title, prefix, or interactive selection.");

        Argument<string?> chatIdentifier = SessionArgument();

        chat.Add(chatIdentifier);

        chat.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Chat(pr.GetValue(chatIdentifier), ct).ConfigureAwait(false));

        session.Add(chat);

        Command entries = new("entries", "List transcript entries for a session.");

        Argument<string?> entriesIdentifier = SessionArgument();

        Option<int?> entriesOffset = new("--offset") { Description = "Number of entries to skip." };

        Option<int?> entriesLimit = new("--limit") { Description = "Maximum entries to return." };

        entries.Add(entriesIdentifier);

        entries.Add(entriesOffset);

        entries.Add(entriesLimit);

        entries.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Entries(
                pr.GetValue(entriesIdentifier),
                pr.GetValue(entriesOffset),
                pr.GetValue(entriesLimit),
                ct).ConfigureAwait(false));

        session.Add(entries);

        Command watch = new("watch", "Watch replayed and live session entries.");

        Argument<string?> watchIdentifier = SessionArgument();

        Option<string?> watchSince = new("--since") { Description = "Resume after this entry GUID." };

        watch.Add(watchIdentifier);

        watch.Add(watchSince);

        watch.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Watch(
                pr.GetValue(watchIdentifier),
                pr.GetValue(watchSince),
                ct).ConfigureAwait(false));

        session.Add(watch);

        Command fork = new("fork", "Fork a session through the server fork API.");

        Argument<string?> forkIdentifier = SessionArgument();

        Option<string?> forkTitle = new("--title") { Description = "Optional fork title." };

        Option<string?> forkUpToEntry = new("--up-to-entry") { Description = "Copy through this entry GUID." };

        Option<string?> forkCampaign = new("--campaign") { Description = "Optional destination campaign GUID." };

        fork.Add(forkIdentifier);

        fork.Add(forkTitle);

        fork.Add(forkUpToEntry);

        fork.Add(forkCampaign);

        fork.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Fork(
                pr.GetValue(forkIdentifier),
                pr.GetValue(forkTitle),
                pr.GetValue(forkUpToEntry),
                pr.GetValue(forkCampaign),
                ct).ConfigureAwait(false));

        session.Add(fork);

        Command rename = new("rename", "Rename a session.");

        Argument<string?> renameIdentifier = SessionArgument();

        Option<string?> renameTitle = new("--title") { Description = "New session title." };

        rename.Add(renameIdentifier);

        rename.Add(renameTitle);

        rename.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Rename(
                pr.GetValue(renameIdentifier),
                pr.GetValue(renameTitle),
                ct).ConfigureAwait(false));

        session.Add(rename);

        Command archive = new("archive", "Archive a session without deleting it.");

        Argument<string?> archiveIdentifier = SessionArgument();

        archive.Add(archiveIdentifier);

        archive.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Archive(pr.GetValue(archiveIdentifier), ct).ConfigureAwait(false));

        session.Add(archive);

        Command export = new("export", "Export an active or archived session.");

        Argument<string?> exportIdentifier = SessionArgument();

        Option<string?> exportFormat = new("--format") { Description = "Export format: json or markdown." };

        export.Add(exportIdentifier);

        export.Add(exportFormat);

        export.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Export(
                pr.GetValue(exportIdentifier),
                pr.GetValue(exportFormat),
                ct).ConfigureAwait(false));

        session.Add(export);

        Command rest = new("rest", "Queue Campaign Log consolidation for a session.");

        Argument<string?> restIdentifier = SessionArgument();

        rest.Add(restIdentifier);

        rest.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Rest(pr.GetValue(restIdentifier), ct).ConfigureAwait(false));

        session.Add(rest);

        Command attachments = new("attachments", "List bound session attachments.");

        Argument<string?> attachmentsIdentifier = SessionArgument();

        attachments.Add(attachmentsIdentifier);

        attachments.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await attachmentHandler
                .List(pr.GetValue(attachmentsIdentifier), ct)
                .ConfigureAwait(false));

        session.Add(attachments);

        AddEntryMutation(
            session,
            "delete-entry",
            "Delete an entry after confirmation.",
            handler.DeleteEntry);

        AddEntryMutation(
            session,
            "pin-entry",
            "Pin an entry when memory management is enabled.",
            handler.PinEntry);

        AddEntryMutation(
            session,
            "unpin-entry",
            "Unpin an entry when memory management is enabled.",
            handler.UnpinEntry);

        Command compact = new("compact", "Compact session context when memory management is enabled.");

        Argument<string?> compactIdentifier = SessionArgument();

        compact.Add(compactIdentifier);

        compact.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Compact(pr.GetValue(compactIdentifier), ct).ConfigureAwait(false));

        session.Add(compact);

        Command divine = new("divine", "Semantic search over Grimoire entries.");

        Argument<string> divineQuery = new("query") { Description = "Semantic search query." };

        Option<int?> divineLimit = new("--limit") { Description = "Maximum number of matches to return." };

        Option<string?> divineCampaign = new("--campaign") { Description = "Restrict the search to this Campaign GUID, exact name, or unique prefix." };

        Option<string?> divineStatus = new("--status") { Description = "Restrict the search to entries in this status." };

        divine.Add(divineQuery);

        divine.Add(divineLimit);

        divine.Add(divineCampaign);

        divine.Add(divineStatus);

        divine.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Divine(
                pr.GetValue(divineQuery)!,
                pr.GetValue(divineLimit),
                ActiveCampaign(sp, pr.GetValue(divineCampaign)),
                pr.GetValue(divineStatus),
                ct).ConfigureAwait(false));

        session.Add(divine);

        return session;

        static Argument<string?> SessionArgument() => new("session")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Optional session GUID, exact title, or unique title prefix; omit for an interactive picker.",
        };

        static void AddEntryMutation(
            Command parent,
            string name,
            string description,
            Func<string?, string?, CancellationToken, Task<int>> action)
        {
            Command command = new(name, description);

            Argument<string?> entry = new("entry")
            {
                Arity = ArgumentArity.ZeroOrOne,
                Description = "Optional entry GUID; omit for an interactive picker.",
            };

            Option<string?> selectedSession = new("--session")
            {
                Description = "Session GUID, exact title, or unique title prefix; omit for an interactive picker.",
            };

            command.Add(entry);

            command.Add(selectedSession);

            command.SetAction(async (ParseResult pr, CancellationToken ct) =>
                await action(
                    pr.GetValue(entry),
                    pr.GetValue(selectedSession),
                    ct).ConfigureAwait(false));

            parent.Add(command);
        }
    }
}
