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
        Option<string?> model = new("--model", "-m") { Description = "Use this configured model instead of the effective context or server default." };
        Option<bool> @new = new("--new", "-n") { Description = "Start a new session rather than continuing the active or last session." };
        Option<bool> unattended = new("--unattended") { Description = "Disable blocking human prompts and apply unattended Ward behavior." };
        Option<string?> campaign = new("--campaign", "-c") { Description = "Use the selected Campaign GUID, name, or unique prefix." };
        Option<string?> workspace = new("--workspace") { Description = "Use the selected Workspace ID, name, or server-host path." };
        Option<string?> session = new("--session") { Description = "Continue the selected Session by GUID, exact title, or unique title prefix." };
        Option<string?> temperature = new("--temperature") { Description = "Sampling temperature from 0 through 2." };
        Option<string?> topP = new("--top-p") { Description = "Nucleus sampling cutoff from 0 through 1." };
        Option<string?> maxTokens = new("--max-tokens") { Description = "Maximum output tokens; any positive integer is accepted." };
        Option<string?> seed = new("--seed") { Description = "Optional signed 64-bit sampling seed; provider support varies." };
        Option<string[]> stop = new("--stop") { AllowMultipleArgumentsPerToken = true, Description = "Stop sequence; repeat the option to supply several sequences." };
        Option<string?> responseFormat = new("--response-format") { Description = "Response format: text, json (alias of json_object), json_object, or json_schema." };
        Option<string?> presencePenalty = new("--presence-penalty") { Description = "Presence penalty from -2 through 2." };
        Option<string?> frequencyPenalty = new("--frequency-penalty") { Description = "Frequency penalty from -2 through 2." };
        Option<string[]> image = new("--image") { AllowMultipleArgumentsPerToken = true, Description = "Local image path to stage as a Scrying focus; repeatable, constrained by configured size and allowed MIME types, and requires a vision-capable model." };
        Option<string[]> attachment = new("--attachment") { Description = "Bound attachment GUID to include; repeatable." };
        Argument<string[]> prompt = new("prompt") { Description = "The prompt words; redirected standard input is attached as additional context." };

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
                null,
                null,
                null,
                null,
                words).ConfigureAwait(false);
        });
        return ask;
    }
}
