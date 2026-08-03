using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildRun(IServiceProvider serviceProvider)
    {

        RunCommand handler = serviceProvider.GetRequiredService<RunCommand>();

        Command command = new(
            "run",
            "Run a prompt through ordinary inference, research, or a named Spell.");

        Argument<string[]> prompt = new("prompt")
        {

            Arity = ArgumentArity.ZeroOrMore,

            Description = "Optional instruction; redirected standard input is attached as additional context.",

        };

        Option<bool> research = new("--research")
        {

            Description = "Route through the bounded server-side web research workflow.",

        };

        Option<string?> spell = new("--spell")
        {

            Description = "Route through an exact or uniquely prefixed Spell name.",

        };

        Option<string[]> with = new("--with")
        {

            Description = "Attach a file as turn-scoped context; repeat as --with @path.",

        };

        Option<bool> dryRun = new("--dry-run")
        {

            Description = "Preview the resolved route and model context without main inference.",

        };

        Option<bool> showContent = new("--show-content")
        {

            Description = "With --dry-run, include model-visible content in the authenticated preview.",

        };

        Option<string?> model = new("--model", "-m");

        Option<bool> newSession = new("--new", "-n");

        Option<bool> unattended = new("--unattended");

        Option<string?> campaign = new("--campaign", "-c");

        Option<string?> workspace = new("--workspace", "-w");

        Option<string?> session = new("--session", "-s");

        Option<string?> temperature = new("--temperature");

        Option<string?> topP = new("--top-p");

        Option<string?> maxTokens = new("--max-tokens");

        Option<string?> seed = new("--seed");

        Option<string[]> stop = new("--stop");

        Option<string?> responseFormat = new("--response-format");

        Option<string?> presencePenalty = new("--presence-penalty");

        Option<string?> frequencyPenalty = new("--frequency-penalty");

        Option<int?> sourceTarget = new("--sources")
        {

            Description = "Optional positive research source target; otherwise continue until source exhaustion.",

        };

        Option<int?> tokenBudget = new("--token-budget")
        {

            Description = "Explicit positive research synthesis output-token budget (default 2000).",

        };

        Option<decimal?> costBudget = new("--cost-budget")
        {

            Description = "Optional research search-provider cost limit in USD.",

        };

        command.Add(prompt);

        command.Add(research);

        command.Add(spell);

        command.Add(with);

        command.Add(dryRun);

        command.Add(showContent);

        command.Add(model);

        command.Add(newSession);

        command.Add(unattended);

        command.Add(campaign);

        command.Add(workspace);

        command.Add(session);

        command.Add(temperature);

        command.Add(topP);

        command.Add(maxTokens);

        command.Add(seed);

        command.Add(stop);

        command.Add(responseFormat);

        command.Add(presencePenalty);

        command.Add(frequencyPenalty);

        command.Add(sourceTarget);

        command.Add(tokenBudget);

        command.Add(costBudget);

        command.SetAction(
            async (ParseResult result, CancellationToken cancellationToken) =>
                await handler.RunAsync(
                    new RunCommandRequest(
                        result.GetValue(prompt) ?? [],
                        result.UnmatchedTokens.ToArray(),
                        result.GetValue(research),
                        result.GetValue(spell),
                        result.GetValue(with) ?? [],
                        result.GetValue(dryRun),
                        result.GetValue(showContent),
                        result.GetValue(model),
                        result.GetValue(newSession),
                        result.GetValue(unattended),
                        result.GetValue(campaign),
                        result.GetValue(workspace),
                        result.GetValue(session),
                        result.GetValue(temperature),
                        result.GetValue(topP),
                        result.GetValue(maxTokens),
                        result.GetValue(seed),
                        result.GetValue(stop) ?? [],
                        result.GetValue(responseFormat),
                        result.GetValue(presencePenalty),
                        result.GetValue(frequencyPenalty),
                        result.GetValue(sourceTarget),
                        result.GetValue(tokenBudget) ?? 2_000,
                        result.GetValue(costBudget)),
                    cancellationToken).ConfigureAwait(false));

        return command;

    }

}
