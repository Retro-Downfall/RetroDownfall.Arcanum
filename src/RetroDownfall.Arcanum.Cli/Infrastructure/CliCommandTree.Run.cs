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

        Option<string[]> attachment = new("--attachment")
        {

            Description = "Bound attachment GUID to include on this turn; repeatable. Use --with @path for a local file.",

        };

        Option<bool> dryRun = new("--dry-run")
        {

            Description = "Preview the resolved route and model context without main inference.",

        };

        Option<bool> showContent = new("--show-content")
        {

            Description = "With --dry-run, include model-visible content in the authenticated preview.",

        };

        Option<string?> model = new("--model", "-m")
        {

            Description = "Use this configured model instead of the effective context or server default.",

        };

        Option<bool> newSession = new("--new", "-n")
        {

            Description = "Start without continuing the effective Session; wins over --session.",

        };

        Option<bool> unattended = new("--unattended")
        {

            Description = "Omit human-prompt tools on the selected live route; Ward records remain informational.",

        };

        Option<bool> continueSession = new("--continue", "-c")
        {

            Description = "Continue the most recent Session. Cannot be combined with --resume or --session.",

        };

        Option<string?> resume = new("--resume", "-r")
        {

            Arity = ArgumentArity.ZeroOrOne,

            Description = "Resume a Session by GUID, exact title, or unique title prefix; omit the value for an interactive picker.",

        };

        Option<string?> campaign = new("--campaign", "-C")
        {

            Description = "Use the selected Campaign GUID, exact name, or unique prefix.",

        };

        Option<string?> workspace = new("--workspace", "-w")
        {

            Description = "Use the selected Workspace ID, name, or server-host path; also the base for relative --with paths.",

        };

        Option<string?> session = new("--session", "-s")
        {

            Description = "Continue the selected Session by GUID, exact title, or unique title prefix.",

        };

        Option<string?> temperature = new("--temperature")
        {

            Description = "Sampling temperature from 0 through 2.",

        };

        Option<string?> topP = new("--top-p")
        {

            Description = "Nucleus sampling cutoff from 0 through 1.",

        };

        Option<string?> maxTokens = new("--max-tokens")
        {

            Description = "Maximum Agent/Spell output tokens; research uses --token-budget.",

        };

        Option<string?> seed = new("--seed")
        {

            Description = "Optional signed 64-bit sampling seed; provider support varies.",

        };

        Option<string[]> stop = new("--stop")
        {

            Description = "Stop sequence; repeat the option to supply several sequences.",

        };

        Option<string?> responseFormat = new("--response-format")
        {

            Description = "Response format: text, json (alias of json_object), json_object, or json_schema.",

        };

        Option<string?> presencePenalty = new("--presence-penalty")
        {

            Description = "Presence penalty from -2 through 2.",

        };

        Option<string?> frequencyPenalty = new("--frequency-penalty")
        {

            Description = "Frequency penalty from -2 through 2.",

        };

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

        command.Add(attachment);

        command.Add(dryRun);

        command.Add(showContent);

        command.Add(model);

        command.Add(newSession);

        command.Add(unattended);

        command.Add(continueSession);

        command.Add(resume);

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
                RejectedPromptOption(serviceProvider, result, prompt)
                ?? await handler.RunAsync(
                    new RunCommandRequest(
                        result.GetValue(prompt) ?? [],
                        result.GetValue(research),
                        result.GetValue(spell),
                        result.GetValue(with) ?? [],
                        result.GetValue(attachment) ?? [],
                        result.GetValue(dryRun),
                        result.GetValue(showContent),
                        result.GetValue(model),
                        result.GetValue(newSession),
                        result.GetValue(unattended),
                        result.GetValue(continueSession),
                        result.GetResult(resume) is not null,
                        result.GetValue(resume),
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
