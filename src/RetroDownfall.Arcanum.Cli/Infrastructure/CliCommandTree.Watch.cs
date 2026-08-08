using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildWatch(IServiceProvider serviceProvider)
    {

        WatchCommands handler = serviceProvider.GetRequiredService<WatchCommands>();

        Command watch = new(
            "watch",
            "Follow an authenticated Arcanum live stream with consistent terminal or NDJSON output.");

        Option<bool> reconnect = new("--reconnect")
        {

            Description = "Reconnect after an unexpected disconnect with capped exponential backoff; possible event gaps are always reported.",

            Recursive = true,

        };

        Option<string[]> eventType = new("--event-type")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Show matching event types; repeat for multiple free-form, case-insensitive values.",

            Recursive = true,

        };

        Option<string[]> toolName = new("--tool")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Show events for matching tool names; repeat for multiple free-form, case-insensitive values.",

            Recursive = true,

        };

        watch.Add(reconnect);

        watch.Add(eventType);

        watch.Add(toolName);

        Command session = new("session", "Follow replayed and live Session entries.");

        Argument<string?> sessionIdentifier = OptionalResourceArgument(
            "session",
            "Session GUID, exact title, or unique title prefix");

        Option<string?> since = new("--since")
        {

            Description = "Begin after this Session Entry GUID.",

        };

        session.Add(sessionIdentifier);

        session.Add(since);

        session.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Session(
                    ActiveSession(serviceProvider, result.GetValue(sessionIdentifier)),
                    result.GetValue(since),
                    WatchOptions(
                        result,
                        reconnect,
                        eventType,
                        toolName),
                    cancellationToken).ConfigureAwait(false));

        watch.Add(session);

        Command apprentice = new("apprentice", "Follow an Apprentice Chronicle.");

        Argument<string?> apprenticeIdentifier = OptionalResourceArgument(
            "apprentice",
            "Apprentice GUID, exact name, or unique name prefix");

        apprentice.Add(apprenticeIdentifier);

        apprentice.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Apprentice(
                    result.GetValue(apprenticeIdentifier),
                    WatchOptions(
                        result,
                        reconnect,
                        eventType,
                        toolName),
                    cancellationToken).ConfigureAwait(false));

        watch.Add(apprentice);

        Command logs = new("logs", "Follow live host log entries.");

        Option<string?> level = new("--level")
        {

            Description = "Minimum server log level (free-form; validated by the API).",

        };

        Option<string?> category = new("--category")
        {

            Description = "Match a log category, case-insensitively.",

        };

        Option<string?> search = new("--search")
        {

            Description = "Search log messages and categories, case-insensitively.",

        };

        logs.Add(level);

        logs.Add(category);

        logs.Add(search);

        logs.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Logs(
                    result.GetValue(level),
                    result.GetValue(category),
                    result.GetValue(search),
                    WatchOptions(
                        result,
                        reconnect,
                        eventType,
                        toolName),
                    cancellationToken).ConfigureAwait(false));

        watch.Add(logs);

        Command mcp = new("mcp", "Follow live MCP server lifecycle events.");

        mcp.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Mcp(
                    WatchOptions(
                        result,
                        reconnect,
                        eventType,
                        toolName),
                    cancellationToken).ConfigureAwait(false));

        watch.Add(mcp);

        Command daemons = new("daemons", "Follow live Unseen Servant daemon events.");

        daemons.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Daemons(
                    WatchOptions(
                        result,
                        reconnect,
                        eventType,
                        toolName),
                    cancellationToken).ConfigureAwait(false));

        watch.Add(daemons);

        Command health = new("health", "Poll authenticated host health snapshots.");

        Option<int?> interval = new("--interval")
        {

            Description = "Seconds between health observations (default: 5; any positive integer).",

        };

        health.Add(interval);

        health.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Health(
                    result.GetValue(interval) ?? 5,
                    WatchOptions(
                        result,
                        reconnect,
                        eventType,
                        toolName),
                    cancellationToken).ConfigureAwait(false));

        watch.Add(health);

        return watch;

    }

    private static WatchCommandOptions WatchOptions(
        ParseResult result,
        Option<bool> reconnect,
        Option<string[]> eventType,
        Option<string[]> toolName) =>
        new(
            result.GetValue(reconnect),
            result.GetValue(eventType) ?? [],
            result.GetValue(toolName) ?? []);

}
