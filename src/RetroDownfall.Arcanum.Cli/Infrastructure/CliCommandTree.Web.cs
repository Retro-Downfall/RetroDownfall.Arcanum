using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildSearch(IServiceProvider services)
    {

        WebWorkflowCommands handler = services
            .GetRequiredService<WebWorkflowCommands>();

        Command command = new(
            "search",
            "Search the live web with bounded results and citations.");

        Argument<string> query = new("query")
        {

            Description = "Search query. Quote multi-word queries.",

        };

        Option<int?> count = new("--count")
        {

            Description = "Maximum results/citations (1-20; default 5).",

        };

        Option<string?> freshness = new("--freshness")
        {

            Description = "Freshness filter: day, week, month, or year.",

        };

        Option<string[]> includeDomain = new("--include-domain")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Restrict search to a domain; repeat for several.",

        };

        Option<string[]> excludeDomain = new("--exclude-domain")
        {

            AllowMultipleArgumentsPerToken = true,

            Description = "Exclude a domain; repeat for several.",

        };

        Option<string?> save = SaveOption();

        Option<string?> attach = AttachSessionOption();

        command.Add(query);

        command.Add(count);

        command.Add(freshness);

        command.Add(includeDomain);

        command.Add(excludeDomain);

        command.Add(save);

        command.Add(attach);

        command.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Search(
                    result.GetValue(query)!,
                    result.GetValue(count) ?? 5,
                    result.GetValue(freshness),
                    result.GetValue(includeDomain) ?? [],
                    result.GetValue(excludeDomain) ?? [],
                    result.GetValue(save),
                    result.GetValue(attach),
                    cancellationToken).ConfigureAwait(false));

        return command;

    }

    private static Command BuildBrowse(IServiceProvider services)
    {

        WebWorkflowCommands handler = services
            .GetRequiredService<WebWorkflowCommands>();

        Command command = new(
            "browse",
            "Read a bounded web page as Markdown with explicit rendering behavior.");

        Argument<string> url = new("url")
        {

            Description = "Absolute HTTP or HTTPS URL.",

        };

        Option<string?> render = new("--render")
        {

            Description = "Rendering mode: static (default) or javascript.",

        };

        Option<string?> save = SaveOption();

        Option<string?> attach = AttachSessionOption();

        command.Add(url);

        command.Add(render);

        command.Add(save);

        command.Add(attach);

        command.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Browse(
                    result.GetValue(url)!,
                    result.GetValue(render) ?? "static",
                    result.GetValue(save),
                    result.GetValue(attach),
                    cancellationToken).ConfigureAwait(false));

        return command;

    }

    private static Command BuildResearch(IServiceProvider services)
    {

        WebWorkflowCommands handler = services
            .GetRequiredService<WebWorkflowCommands>();

        Command command = new(
            "research",
            "Run progress-driven server-side research with citations and cancellation.");

        Argument<string> question = new("question")
        {

            Description = "Research question. Quote multi-word questions.",

        };

        Option<int?> sourceTarget = new("--sources")
        {

            Description = "Optional positive target for unique sources; otherwise continue until source exhaustion.",

        };

        Option<string?> model = new("--model")
        {

            Description = "Server-configured model for final synthesis.",

        };

        Option<int?> tokenBudget = new("--token-budget")
        {

            Description = "Explicit positive synthesis output-token budget (default 2000).",

        };

        Option<decimal?> costBudget = new("--cost-budget")
        {

            Description = "Maximum reported search-provider cost in USD.",

        };

        Option<string?> continueSession = new("--continue-session")
        {

            Description = "Continue an existing session by GUID, exact title, or unique prefix.",

        };

        Option<string?> format = new("--format")
        {

            Description = "Final output: terminal (default), markdown, or json.",

        };

        Option<string?> save = SaveOption();

        Option<string?> attach = AttachSessionOption();

        command.Add(question);

        command.Add(sourceTarget);

        command.Add(model);

        command.Add(tokenBudget);

        command.Add(costBudget);

        command.Add(continueSession);

        command.Add(format);

        command.Add(save);

        command.Add(attach);

        command.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.Research(
                    result.GetValue(question)!,
                    result.GetValue(sourceTarget),
                    result.GetValue(model),
                    result.GetValue(tokenBudget) ?? 2_000,
                    result.GetValue(costBudget),
                    result.GetValue(continueSession),
                    result.GetValue(format),
                    result.GetValue(save),
                    result.GetValue(attach),
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    cancellationToken).ConfigureAwait(false));

        return command;

    }

    private static Option<string?> SaveOption() =>
        new("--save")
        {

            Description = "Atomically save the final Markdown content to a local path.",

        };

    private static Option<string?> AttachSessionOption() =>
        new("--attach-to-session")
        {

            Description = "Persist the final Markdown as an attachment on a session.",

        };

}
