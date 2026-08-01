using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildAsk(IServiceProvider sp)
    {
        AskCommand handler = sp.GetRequiredService<AskCommand>();
        Command ask = new("ask", "Ask the Mage.");
        Option<string?> model = new("--model", "-m");
        Option<bool> @new = new("--new", "-n");
        Option<bool> unattended = new("--unattended");
        Option<string?> campaign = new("--campaign", "-c");
        Option<string?> workspace = new("--workspace");
        Option<string?> session = new("--session");
        Option<string?> temperature = new("--temperature");
        Option<string?> topP = new("--top-p");
        Option<string?> maxTokens = new("--max-tokens");
        Option<string?> seed = new("--seed");
        Option<string[]> stop = new("--stop") { AllowMultipleArgumentsPerToken = true };
        Option<string?> responseFormat = new("--response-format");
        Option<string?> presencePenalty = new("--presence-penalty");
        Option<string?> frequencyPenalty = new("--frequency-penalty");
        Option<string[]> image = new("--image") { AllowMultipleArgumentsPerToken = true };
        Option<string[]> attachment = new("--attachment");
        Argument<string[]> prompt = new("prompt");

        ask.Add(model); ask.Add(@new); ask.Add(unattended); ask.Add(campaign);
        ask.Add(workspace); ask.Add(session);
        ask.Add(temperature); ask.Add(topP); ask.Add(maxTokens); ask.Add(seed);
        ask.Add(stop); ask.Add(responseFormat); ask.Add(presencePenalty);
        ask.Add(frequencyPenalty); ask.Add(image); ask.Add(attachment); ask.Add(prompt);

        ask.SetAction(async (ParseResult pr, CancellationToken ct) =>
        {
            string[] words = pr.GetValue(prompt) ?? [];
            string[] escaped = pr.UnmatchedTokens.ToArray();
            return await handler.Ask(
                escaped,
                ct,
                pr.GetValue(model),
                pr.GetValue(@new),
                pr.GetValue(unattended),
                pr.GetValue(campaign),
                pr.GetValue(workspace),
                pr.GetValue(session),
                pr.GetValue(temperature),
                pr.GetValue(topP),
                pr.GetValue(maxTokens),
                pr.GetValue(seed),
                pr.GetValue(stop) ?? [],
                pr.GetValue(responseFormat),
                pr.GetValue(presencePenalty),
                pr.GetValue(frequencyPenalty),
                pr.GetValue(image) ?? [],
                pr.GetValue(attachment) ?? [],
                words).ConfigureAwait(false);
        });
        return ask;
    }
}
