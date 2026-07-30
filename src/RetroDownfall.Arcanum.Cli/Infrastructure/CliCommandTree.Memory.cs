using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
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
            pr.GetValue(session),
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
        Option<string?> severity = new("--severity", "-s") { Description = "Severity: Info, Warning, or Critical." };
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
