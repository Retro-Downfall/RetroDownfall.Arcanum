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

        MemoryCommands handler = sp.GetRequiredService<MemoryCommands>();

        Command memory = new(
            "memory",
            "Inspect distinct Arcanum memory sources and retention policies.");

        Argument<string?> statusSession = OptionalMemorySessionArgument();

        Command status = new("status", "Show feature gates and counts by memory store.");

        status.Add(statusSession);

        status.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Status(
                    ActiveSession(sp, pr.GetValue(statusSession)),
                    ct).ConfigureAwait(false));

        Argument<string?> sourcesSession = OptionalMemorySessionArgument();

        Command sources = new("sources", "Describe provenance and retention for every memory source.");

        sources.Add(sourcesSession);

        sources.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Sources(
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
                await handler.Search(
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
                await handler.Explain(
                    ActiveSession(sp, pr.GetValue(explainSession)),
                    ct).ConfigureAwait(false));

        Command lexicon = new("lexicon", "Inspect or explicitly delete Lexicon entities.");

        Command lexiconList = new("list", "List Lexicon entities.");

        lexiconList.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.LexiconList(ct).ConfigureAwait(false));

        Command lexiconShow = new("show", "Show a Lexicon entity by name.");

        Argument<string> showName = new("name") { Description = "Lexicon entity name." };

        lexiconShow.Add(showName);

        lexiconShow.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.LexiconShow(
                    pr.GetValue(showName)!,
                    ct).ConfigureAwait(false));

        Command lexiconSearch = new("search", "Search Lexicon names, types, and facts.");

        Argument<string> lexiconQuery = new("query") { Description = "Search query text." };

        lexiconSearch.Add(lexiconQuery);

        lexiconSearch.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.LexiconSearch(
                    pr.GetValue(lexiconQuery)!,
                    ct).ConfigureAwait(false));

        Command lexiconDelete = new("delete", "Delete one explicitly named Lexicon entity.");

        Argument<string> deleteName = new("name") { Description = "Lexicon entity name." };

        lexiconDelete.Add(deleteName);

        lexiconDelete.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.LexiconDelete(
                    pr.GetValue(deleteName)!,
                    ct).ConfigureAwait(false));

        lexicon.Add(lexiconList);

        lexicon.Add(lexiconShow);

        lexicon.Add(lexiconSearch);

        lexicon.Add(lexiconDelete);

        memory.Add(status);

        memory.Add(sources);

        memory.Add(search);

        memory.Add(explain);

        memory.Add(lexicon);

        return memory;

    }

    private static Argument<string?> OptionalMemorySessionArgument() => new("session")
    {
        Arity = ArgumentArity.ZeroOrOne,
        Description = "Optional session GUID, exact title, or unique title prefix.",
    };

    private static Command BuildSaga(IServiceProvider sp)
    {
        SagaCommands handler = sp.GetRequiredService<SagaCommands>();
        Command saga = new("saga", "Saga long-term associative memory (requires arcanum serve).");

        Command list = new("list", "Paginated listing of Saga memories.");
        Option<string?> listQuery = new("--query") { Description = "Free-text query." };
        Option<string?> session = new("--session") { Description = "Filter by session GUID." };
        Option<int?> listLimit = new("--limit") { Description = "Maximum number of memories to return." };
        Option<int?> offset = new("--offset") { Description = "Pagination offset." };
        list.Add(listQuery); list.Add(session); list.Add(listLimit); list.Add(offset);
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(
            pr.GetValue(listQuery),
            ActiveSession(sp, pr.GetValue(session)),
            pr.GetValue(listLimit),
            pr.GetValue(offset),
            ct).ConfigureAwait(false));

        Command divine = new("divine", "Semantic search over Saga memories.");
        Argument<string> query = new("query") { Description = "Search query text." };
        Option<int?> divineLimit = new("--limit") { Description = "Maximum number of results to return." };
        divine.Add(query); divine.Add(divineLimit);
        divine.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Divine(
            pr.GetValue(query)!,
            pr.GetValue(divineLimit),
            ct).ConfigureAwait(false));

        Command delete = new("delete", "Delete a single Saga memory.");
        Argument<string> id = new("id") { Description = "Saga memory ID." };
        delete.Add(id);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Delete(
            pr.GetValue(id)!,
            ct).ConfigureAwait(false));

        Command stats = new("stats", "Aggregate summary of Saga memory storage.");
        stats.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Stats(ct).ConfigureAwait(false));

        saga.Add(list); saga.Add(divine); saga.Add(delete); saga.Add(stats);
        return saga;
    }

    private static Command BuildLore(IServiceProvider sp)
    {
        LoreCommands handler = sp.GetRequiredService<LoreCommands>();
        Command lore = new("lore", "Manage Grimoire explicit memory (lore) directly.");

        Command list = new("list", "List all scribed lore keys.");
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(ct).ConfigureAwait(false));

        Command get = new("get", "Read a specific lore entry by key.");
        Argument<string> getKey = new("key") { Description = "The lore key." };
        get.Add(getKey);
        get.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Get(
            pr.GetValue(getKey)!,
            ct).ConfigureAwait(false));

        Command set = new("set", "Create or update a lore entry.");
        Argument<string> setKey = new("key") { Description = "The lore key." };
        Argument<string> value = new("value") { Description = "The lore value." };
        set.Add(setKey); set.Add(value);
        set.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Set(
            pr.GetValue(setKey)!,
            pr.GetValue(value)!,
            ct).ConfigureAwait(false));

        Command delete = new("delete", "Delete a lore entry.");
        Argument<string> deleteKey = new("key") { Description = "The lore key." };
        delete.Add(deleteKey);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Delete(
            pr.GetValue(deleteKey)!,
            ct).ConfigureAwait(false));

        lore.Add(list); lore.Add(get); lore.Add(set); lore.Add(delete);
        return lore;
    }

    private static Command BuildDaemon(IServiceProvider sp)
    {
        DaemonCommands handler = sp.GetRequiredService<DaemonCommands>();
        Command daemon = new("daemon", "Manage the Arcanum background daemon.");

        Command install = new("install", "Install and start the Arcanum background daemon.");
        install.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Install(ct).ConfigureAwait(false));

        Command uninstall = new("uninstall", "Stop and uninstall the Arcanum background daemon.");
        uninstall.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Uninstall(ct).ConfigureAwait(false));

        Command status = new("status", "Show whether the Arcanum daemon is running.");
        status.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Status(ct).ConfigureAwait(false));

        Command jobs = new("jobs", "List Unseen Servant jobs (requires API: arcanum serve).");
        jobs.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Jobs(ct).ConfigureAwait(false));

        Command initiative = new("initiative", "Set adaptive polling interval for a job (requires API: arcanum serve).");
        Argument<string> jobName = new("job-name") { Description = "The Unseen Servant job name." };
        Argument<int> minutes = new("minutes") { Description = "The new polling interval in minutes (>= 1)." };
        initiative.Add(jobName); initiative.Add(minutes);
        initiative.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Initiative(
            pr.GetValue(jobName)!,
            pr.GetValue(minutes),
            ct).ConfigureAwait(false));

        Command alert = new("alert", "Send a Comm Link test alert (requires API: arcanum serve).");
        Argument<string> message = new("message") { Description = "The alert message." };
        Option<string?> title = new("--title", "-t") { Description = "Alert title." };
        Option<string?> severity = new("--severity") { Description = "Severity: Info, Warning, or Critical." };
        Option<string?> source = new("--source") { Description = "The alert source label." };
        alert.Add(message); alert.Add(title); alert.Add(severity); alert.Add(source);
        alert.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.Alert(
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
