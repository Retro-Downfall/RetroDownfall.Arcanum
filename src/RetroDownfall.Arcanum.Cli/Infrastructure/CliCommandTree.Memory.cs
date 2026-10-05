using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Commands.Tower;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildMemory(IServiceProvider sp)
    {
        DeferredHandler<MemoryCommands> handler = new(sp);

        Command memory = new(
            "memory",
            "Inspect distinct Arcanum memory sources and retention policies.");

        Argument<string?> statusSession = OptionalMemorySessionArgument();

        Command status = new("status", "Show feature gates and counts by memory store.");

        status.Add(statusSession);

        status.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.Status(
                    ActiveSession(sp, pr.GetValue(statusSession)),
                    ct).ConfigureAwait(false));

        Argument<string?> sourcesSession = OptionalMemorySessionArgument();

        Command sources = new("sources", "Describe provenance and retention for every memory source.");

        sources.Add(sourcesSession);

        sources.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.Sources(
                    ActiveSession(sp, pr.GetValue(sourcesSession)),
                    ct).ConfigureAwait(false));

        Command search = new("search", "Search persisted memory with an explicit or displayed scope.");

        Argument<string> query = new("query") { Description = "Search query text." };

        Option<string?> scope = new("--scope")
        {
            Description = "session, attachments, workspace, saga, lexicon, or all (default).",
        };

        Option<string?> searchSession = new("--session")
        {
            Description = "Optional session GUID, exact title, or unique title prefix.",
        };

        Option<string?> workspace = new("--workspace")
        {
            Description = "Optional workspace ID; omit to search every indexed workspace.",
        };

        search.Add(query);

        search.Add(scope);

        search.Add(searchSession);

        search.Add(workspace);

        search.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.Search(
                    pr.GetValue(query)!,
                    pr.GetValue(scope),
                    ActiveSession(sp, pr.GetValue(searchSession)),
                    pr.GetValue(workspace),
                    ct).ConfigureAwait(false));

        Argument<string?> explainSession = OptionalMemorySessionArgument();

        Command explain = new("explain", "Explain what can be eligible for the next turn and why.");

        explain.Add(explainSession);

        explain.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.Explain(
                    ActiveSession(sp, pr.GetValue(explainSession)),
                    ct).ConfigureAwait(false));

        Command lexicon = new("lexicon", "Inspect, curate, or explicitly delete Lexicon entities.");

        Command lexiconList = new("list", "List Lexicon entities.");

        lexiconList.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.LexiconList(ct).ConfigureAwait(false));

        Command lexiconShow = new("show", "Show one exact Global or Campaign Lexicon entity and its curation target.");

        Argument<string> showName = new("name") { Description = "Lexicon entity name." };

        Option<Guid?> showCampaign = LexiconCampaignOption();

        lexiconShow.Add(showName);

        lexiconShow.Add(showCampaign);

        lexiconShow.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.LexiconShow(
                    pr.GetValue(showName)!,
                    pr.GetValue(showCampaign),
                    ct).ConfigureAwait(false));

        Command lexiconSearch = new("search", "Search Lexicon names, types, and facts.");

        Argument<string> lexiconQuery = new("query") { Description = "Search query text." };

        lexiconSearch.Add(lexiconQuery);

        lexiconSearch.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.LexiconSearch(
                    pr.GetValue(lexiconQuery)!,
                    ct).ConfigureAwait(false));

        Command lexiconDelete = new("delete", "Delete one explicitly named Lexicon entity without erasing it.");

        Argument<string> deleteName = new("name") { Description = "Lexicon entity name." };

        lexiconDelete.Add(deleteName);

        lexiconDelete.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.LexiconDelete(
                    pr.GetValue(deleteName)!,
                    ct).ConfigureAwait(false));

        lexicon.Add(lexiconList);

        lexicon.Add(lexiconShow);

        lexicon.Add(lexiconSearch);

        lexicon.Add(lexiconDelete);

        lexicon.Add(BuildLexiconReview(handler));

        Command lexiconCorrect = new("correct", "Replace the type and complete facts of one exact Lexicon entry after inspection.");

        Argument<string> correctName = new("name") { Description = "Lexicon entity name." };

        Option<Guid?> correctCampaign = LexiconCampaignOption();

        Option<string> correctFile = new("--file", "-f")
        {
            Required = true,
            Description = "Read replacement JSON containing type and facts from this path, or use - for stdin (requires --yes).",
        };

        lexiconCorrect.Add(correctName);

        lexiconCorrect.Add(correctCampaign);

        lexiconCorrect.Add(correctFile);

        lexiconCorrect.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Value.LexiconCorrect(pr.GetValue(correctName)!, pr.GetValue(correctCampaign),
                pr.GetValue(correctFile)!, ct).ConfigureAwait(false));

        lexicon.Add(lexiconCorrect);

        AddLexiconMutation(lexicon, "retire", "Remove an exact Lexicon entry from retrieval while retaining it for inspection.", (first, second, cancellationToken) => handler.Value.LexiconRetire(first, second, cancellationToken));

        AddLexiconMutation(lexicon, "reinstate", "Make a retired exact Lexicon entry eligible for retrieval again.", (first, second, cancellationToken) => handler.Value.LexiconReinstate(first, second, cancellationToken));

        AddLexiconMutation(lexicon, "pin", "Protect an exact Lexicon entry from automatic retention pruning.", (first, second, cancellationToken) => handler.Value.LexiconPin(first, second, cancellationToken));

        AddLexiconMutation(lexicon, "unpin", "Release an exact Lexicon entry's protection from automatic retention pruning.", (first, second, cancellationToken) => handler.Value.LexiconUnpin(first, second, cancellationToken));

        Command lexiconErase = new(
            "erase",
            "Erase one exact Lexicon entry for good, so extraction and agents cannot write its name again in that scope.");

        Argument<string> eraseName = new("name") { Description = "Lexicon entity name." };

        Option<Guid?> eraseCampaign = LexiconCampaignOption();

        lexiconErase.Add(eraseName);

        lexiconErase.Add(eraseCampaign);

        lexiconErase.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.LexiconErase(
                    pr.GetValue(eraseName)!,
                    pr.GetValue(eraseCampaign),
                    ct).ConfigureAwait(false));

        lexicon.Add(lexiconErase);

        Command lexiconRelease = new(
            "release",
            "Release one exact Lexicon name's erasure fingerprint, so extraction and agents may write it again in that scope.");

        Argument<string> releaseName = new("name") { Description = "Lexicon entity name." };

        Option<Guid?> releaseCampaign = LexiconCampaignOption();

        lexiconRelease.Add(releaseName);

        lexiconRelease.Add(releaseCampaign);

        lexiconRelease.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.LexiconRelease(
                    pr.GetValue(releaseName)!,
                    pr.GetValue(releaseCampaign),
                    ct).ConfigureAwait(false));

        lexicon.Add(lexiconRelease);

        memory.Add(status);

        memory.Add(sources);

        memory.Add(search);

        memory.Add(explain);

        memory.Add(BuildCovenant(sp));

        memory.Add(lexicon);

        memory.Add(BuildMemorySagaCuration(sp));

        memory.Add(BuildMemoryErasure(handler));

        return memory;
    }

    /// <summary>
    /// The <c>memory erasure</c> subgroup: the erasure evidence that belongs to no one store.
    /// </summary>
    /// <remarks>
    /// <c>status</c> reads, <c>scrub</c> finishes what an erase left pending on the log, and
    /// <c>reset-key</c> discards the evidence the current key cannot verify. Only the last asks first:
    /// it is the one that cannot be undone.
    /// </remarks>
    private static Command BuildMemoryErasure(DeferredHandler<MemoryCommands> handler)
    {
        Command erasure = new(
            "erasure",
            "Inspect and administer erasure fingerprints and receipts across the memory stores.");

        Command status = new(
            "status",
            "Show the erasure key's state and each store's fingerprint, unverifiable and receipt counts.");

        status.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.ErasureStatus(ct).ConfigureAwait(false));

        Command scrub = new(
            "scrub",
            "Retry the write-ahead-log checkpoint erasures left pending, and report what it verified.");

        scrub.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.ErasureScrub(ct).ConfigureAwait(false));

        Command resetKey = new(
            "reset-key",
            "Discard the erasure evidence the current key cannot verify, creating a key when none exists.");

        resetKey.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.ErasureResetKey(ct).ConfigureAwait(false));

        erasure.Add(status);

        erasure.Add(scrub);

        erasure.Add(resetKey);

        return erasure;
    }

    private static Argument<string?> OptionalMemorySessionArgument() => new("session")
    {
        Arity = ArgumentArity.ZeroOrOne,
        Description = "Optional session GUID, exact title, or unique title prefix.",
    };

    private static Option<Guid?> LexiconCampaignOption() => new("--campaign", "-C")
    {
        Description = "Exact Campaign GUID. Omit for exact Global scope; saved and active context are never used.",
    };

    private static void AddLexiconMutation(Command lexicon, string verb, string description,
        Func<string, Guid?, CancellationToken, Task<int>> handler)
    {
        Command command = new(verb, description);

        Argument<string> name = new("name") { Description = "Lexicon entity name." };

        Option<Guid?> campaign = LexiconCampaignOption();

        command.Add(name);

        command.Add(campaign);

        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler(pr.GetValue(name)!, pr.GetValue(campaign), ct).ConfigureAwait(false));

        lexicon.Add(command);
    }

    /// <summary>
    /// The <c>memory saga</c> subgroup: curation over one Saga memory at a time.
    /// </summary>
    /// <remarks>
    /// A subgroup of <c>memory</c> rather than a verb on the top-level <c>saga</c> command, because
    /// <c>saga</c> is that store's own read-and-delete surface and answers a different question: what is
    /// in there, and take this out of it. Curation answers what one memory is, and what the operator has
    /// decided about it — the same split the routes make, and the same place the Covenant's and the
    /// Lexicon's curation verbs already sit.
    ///
    /// <para><c>correct</c> takes no content argument. The replacement text arrives through
    /// <c>--file</c> or piped standard input, so a corrected memory never lands in shell history or in
    /// the process list of a shared machine.</para>
    /// </remarks>
    private static Command BuildMemorySagaCuration(IServiceProvider sp)
    {
        DeferredHandler<MemoryCommands> handler = new(sp);

        Command saga = new(
            "saga",
            "Curate one Saga memory: read it, correct it, and decide whether retrieval and retention keep it.");

        Command show = new("show", "Show one Saga memory's provenance, lifecycle, and retrieval eligibility.");

        Argument<string> showId = SagaMemoryIdArgument();

        show.Add(showId);

        show.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.SagaShow(
                    pr.GetValue(showId)!,
                    ct).ConfigureAwait(false));

        Command correct = new(
            "correct",
            "Replace the text of one Saga memory, naming the exact content being corrected.");

        Argument<string> correctId = SagaMemoryIdArgument();

        Option<string> correctHash = ExpectedContentHashOption();

        Option<string?> correctFile = new("--file", "-f")
        {
            Description = "Read the replacement text from this file. Omit to read from piped standard input.",
        };

        correct.Add(correctId);

        correct.Add(correctHash);

        correct.Add(correctFile);

        correct.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.SagaCorrect(
                    pr.GetValue(correctId)!,
                    pr.GetValue(correctHash)!,
                    pr.GetValue(correctFile),
                    ct).ConfigureAwait(false));

        Command retire = new("retire", "Take one Saga memory out of retrieval, keeping it inspectable.");

        Argument<string> retireId = SagaMemoryIdArgument();

        Option<string> retireHash = ExpectedContentHashOption();

        retire.Add(retireId);

        retire.Add(retireHash);

        retire.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.SagaRetire(
                    pr.GetValue(retireId)!,
                    pr.GetValue(retireHash)!,
                    ct).ConfigureAwait(false));

        Command reinstate = new("reinstate", "Put a retired Saga memory back into retrieval.");

        Argument<string> reinstateId = SagaMemoryIdArgument();

        Option<string> reinstateHash = ExpectedContentHashOption();

        reinstate.Add(reinstateId);

        reinstate.Add(reinstateHash);

        reinstate.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.SagaReinstate(
                    pr.GetValue(reinstateId)!,
                    pr.GetValue(reinstateHash)!,
                    ct).ConfigureAwait(false));

        Command pin = new("pin", "Mark one Saga memory durable, so retention will not prune it.");

        Argument<string> pinId = SagaMemoryIdArgument();

        pin.Add(pinId);

        pin.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.SagaPin(
                    pr.GetValue(pinId)!,
                    ct).ConfigureAwait(false));

        Command unpin = new("unpin", "Release a pin, so retention may prune this memory again.");

        Argument<string> unpinId = SagaMemoryIdArgument();

        unpin.Add(unpinId);

        unpin.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.SagaUnpin(
                    pr.GetValue(unpinId)!,
                    ct).ConfigureAwait(false));

        saga.Add(show);

        saga.Add(correct);

        saga.Add(retire);

        saga.Add(reinstate);

        Command erase = new(
            "erase",
            "Erase one Saga memory and its identical-content twins for good, so extraction cannot write that content again in its scope.");

        Argument<string> eraseId = SagaMemoryIdArgument();

        // Optional here, unlike on correct: omitted, the hash show just reported is sent, which still
        // binds the erase to the text the host holds.
        Option<string?> eraseHash = new("--expected-content-hash")
        {
            Description = "The content hash 'memory saga show' printed for the text you read. Omit to use the hash shown now.",
        };

        erase.Add(eraseId);

        erase.Add(eraseHash);

        erase.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.SagaErase(
                    pr.GetValue(eraseId)!,
                    pr.GetValue(eraseHash),
                    ct).ConfigureAwait(false));

        Command release = new(
            "release",
            "Release the erasure fingerprint of one Saga content in one exact scope, so extraction may write it again there.");

        Option<string> releaseFile = new("--file", "-f")
        {
            Required = true,
            Description = "Read the erased content from this file, or use - for stdin (requires --yes). Sent exactly as read.",
        };

        Option<Guid?> releaseCampaign = new("--campaign", "-C")
        {
            Description = "Exact Campaign GUID. Alone it means Campaign scope; saved and active context are never used.",
        };

        Option<string?> releaseScope = new("--scope")
        {
            Description = "global, campaign, unresolved, or unclassified. Omit for Global, or for Campaign when --campaign is given.",
        };

        release.Add(releaseFile);

        release.Add(releaseCampaign);

        release.Add(releaseScope);

        release.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Value.SagaRelease(
                    pr.GetValue(releaseFile)!,
                    pr.GetValue(releaseCampaign),
                    pr.GetValue(releaseScope),
                    ct).ConfigureAwait(false));

        saga.Add(pin);

        saga.Add(unpin);

        saga.Add(erase);

        saga.Add(release);

        saga.Add(BuildSagaReview(handler));

        return saga;
    }

    private static Argument<string> SagaMemoryIdArgument() => new("id")
    {
        Description = "Saga memory ID.",
    };

    /// <summary>
    /// The digest of the content the operator read before deciding to change it.
    /// </summary>
    /// <remarks>
    /// Required rather than defaulted, because there is no value that means "whatever is there now": the
    /// point of the flag is that the host compares it inside the write transaction, so an omitted one
    /// could only be a request to skip the comparison. <c>memory saga show</c> prints the digest to pass
    /// here.
    /// </remarks>
    private static Option<string> ExpectedContentHashOption() => new("--expected-content-hash")
    {
        Description = "The content hash 'memory saga show' printed for the text you read.",
        Required = true,
    };

    private static Command BuildSaga(IServiceProvider sp)
    {
        DeferredHandler<SagaCommands> handler = new(sp);
        Command saga = new("saga", "Saga long-term associative memory (requires arcanum serve).");

        Command list = new("list", "Paginated listing of Saga memories.");
        Option<string?> listQuery = new("--query") { Description = "Free-text query." };
        Option<string?> session = new("--session") { Description = "Filter by session GUID." };
        Option<int?> listLimit = new("--limit") { Description = "Maximum number of memories to return." };
        Option<int?> offset = new("--offset") { Description = "Pagination offset." };
        list.Add(listQuery); list.Add(session); list.Add(listLimit); list.Add(offset);
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.List(
            pr.GetValue(listQuery),
            ActiveSession(sp, pr.GetValue(session)),
            pr.GetValue(listLimit),
            pr.GetValue(offset),
            ct).ConfigureAwait(false));

        Command divine = new("divine", "Semantic search over Saga memories.");
        Argument<string> query = new("query") { Description = "Search query text." };
        Option<int?> divineLimit = new("--limit") { Description = "Maximum number of results to return." };
        Option<string?> divineSession = new("--session") { Description = "Search as this session, honoring the Campaign scope its turns draw from." };
        divine.Add(query); divine.Add(divineLimit); divine.Add(divineSession);
        divine.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Divine(
            pr.GetValue(query)!,
            pr.GetValue(divineLimit),
            ActiveSession(sp, pr.GetValue(divineSession)),
            ct).ConfigureAwait(false));

        Command delete = new("delete", "Delete a single Saga memory without erasing it.");
        Argument<string> id = new("id") { Description = "Saga memory ID." };
        delete.Add(id);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Delete(
            pr.GetValue(id)!,
            ct).ConfigureAwait(false));

        Command stats = new("stats", "Aggregate summary of Saga memory storage.");
        stats.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Stats(ct).ConfigureAwait(false));

        saga.Add(list); saga.Add(divine); saga.Add(delete); saga.Add(stats);
        return saga;
    }

    private static Command BuildLore(IServiceProvider sp)
    {
        DeferredHandler<LoreCommands> handler = new(sp);
        Command lore = new("lore", "Manage Grimoire explicit memory (lore) directly.");

        Command list = new("list", "List all scribed lore keys.");
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.List(ct).ConfigureAwait(false));

        Command get = new("get", "Read a specific lore entry by key.");
        Argument<string> getKey = new("key") { Description = "The lore key." };
        get.Add(getKey);
        get.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Get(
            pr.GetValue(getKey)!,
            ct).ConfigureAwait(false));

        Command set = new("set", "Create or update a lore entry.");
        Argument<string> setKey = new("key") { Description = "The lore key." };
        Argument<string> value = new("value") { Description = "The lore value." };
        set.Add(setKey); set.Add(value);
        set.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Set(
            pr.GetValue(setKey)!,
            pr.GetValue(value)!,
            ct).ConfigureAwait(false));

        Command delete = new("delete", "Delete a lore entry.");
        Argument<string> deleteKey = new("key") { Description = "The lore key." };
        delete.Add(deleteKey);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Delete(
            pr.GetValue(deleteKey)!,
            ct).ConfigureAwait(false));

        lore.Add(list); lore.Add(get); lore.Add(set); lore.Add(delete);
        return lore;
    }

    private static Command BuildDaemon(IServiceProvider sp)
    {
        DeferredHandler<DaemonCommands> handler = new(sp);
        Command daemon = new("daemon", "Manage the Arcanum background daemon.");

        Command install = new("install", "Install and start the Arcanum background daemon.");
        install.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Install(ct).ConfigureAwait(false));

        Command uninstall = new("uninstall", "Stop and uninstall the Arcanum background daemon.");
        uninstall.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Uninstall(ct).ConfigureAwait(false));

        Command status = new("status", "Show whether the Arcanum daemon is running.");
        status.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Status(ct).ConfigureAwait(false));

        Command jobs = new("jobs", "List Unseen Servant jobs (requires API: arcanum serve).");
        jobs.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Jobs(ct).ConfigureAwait(false));

        Command initiative = new("initiative", "Set adaptive polling interval for a job (requires API: arcanum serve).");
        Argument<string> jobName = new("job-name") { Description = "The Unseen Servant job name." };
        Argument<int> minutes = new("minutes") { Description = "The new polling interval in minutes (>= 1)." };
        initiative.Add(jobName); initiative.Add(minutes);
        initiative.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Initiative(
            pr.GetValue(jobName)!,
            pr.GetValue(minutes),
            ct).ConfigureAwait(false));

        Command alert = new("alert", "Send a Comm Link test alert (requires API: arcanum serve).");
        Argument<string> message = new("message") { Description = "The alert message." };
        Option<string?> title = new("--title", "-t") { Description = "Alert title." };
        Option<string?> severity = new("--severity") { Description = "Severity: Info, Warning, or Critical." };
        Option<string?> source = new("--source") { Description = "The alert source label." };
        alert.Add(message); alert.Add(title); alert.Add(severity); alert.Add(source);
        alert.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Value.Alert(
            pr.GetValue(message)!,
            pr.GetValue(title) ?? "Arcanum alert",
            pr.GetValue(severity) ?? "Warning",
            pr.GetValue(source) ?? "cli:daemon alert",
            ct).ConfigureAwait(false));

        daemon.Add(install); daemon.Add(uninstall); daemon.Add(status);
        daemon.Add(jobs); daemon.Add(initiative); daemon.Add(alert);
        return daemon;
    }
}
