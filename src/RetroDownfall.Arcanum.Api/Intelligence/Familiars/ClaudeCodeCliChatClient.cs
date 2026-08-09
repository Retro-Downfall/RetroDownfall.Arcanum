using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Api.Intelligence.Familiars;

/// <summary>
/// Runs a turn through the operator's Claude Code CLI in headless print mode.
/// </summary>
/// <remarks>
/// Pinned against <c>claude 2.1.220</c>. <c>--output-format stream-json</c> emits one JSON object
/// per line and <c>--include-partial-messages</c> adds token-level <c>stream_event</c> frames, which
/// is what lets a Familiar stream like any other provider instead of delivering one block at the
/// end. The CLI's own agent loop is switched off — no tools, no slash commands, no MCP servers —
/// because Arcanum owns the tool loop and delegating into the CLI's is an explicit non-goal.
/// </remarks>
internal sealed class ClaudeCodeCliChatClient(
    IFamiliarProcessRunner runner,
    ProviderSettings provider,
    string resolvedModel,
    IReadOnlyList<string> deniedEnvironmentVariables)
    : FamiliarChatClient(runner, provider, resolvedModel, deniedEnvironmentVariables)
{

    protected override FamiliarProcessRequest BuildRequest(FamiliarPrompt prompt, string? jsonSchema)
    {

        List<string> arguments =
        [
            "--print",

            "--output-format",
            "stream-json",

            // stream-json requires verbose output; without it the CLI refuses the combination.
            "--verbose",

            "--include-partial-messages",

            "--model",
            ResolvedModel,

            // Arcanum's tool loop is the only tool loop. An empty list disables the built-in set.
            "--tools",
            string.Empty,

            "--disable-slash-commands",

            // User settings only. Project and local settings come from the working directory, and
            // their `hooks` block runs shell commands on every turn — so a repository must never be
            // able to execute code just because Arcanum spawned a Familiar near it.
            "--setting-sources",
            "user",

            // Only MCP servers passed on this command line may load, and none are.
            "--strict-mcp-config",

            // The Grimoire is Arcanum's session store; the Familiar must not keep a second one.
            "--no-session-persistence",
        ];

        if (prompt.SystemPrompt is { Length: > 0 } systemPrompt)
        {

            arguments.Add("--system-prompt");

            arguments.Add(systemPrompt);

        }

        if (jsonSchema is { Length: > 0 })
        {

            // The CLI validates the answer against the schema itself, so a structured-output turn
            // does not have to survive Arcanum's retry loop to come back well-formed.
            arguments.Add("--json-schema");

            arguments.Add(jsonSchema);

        }

        return new FamiliarProcessRequest
        {

            FileName = FamiliarProviders.ResolveCommand(Provider),

            Arguments = arguments,

            StandardInput = prompt.Text,

            WorkingDirectory = WorkingDirectory,

        };

    }

    protected override IEnumerable<ChatResponseUpdate> ProjectFrame(string line, FamiliarTurnState state)
    {

        ClaudeCodeFrame? frame = TryParse(line);

        if (frame is null)
        {
            yield break;
        }

        switch (frame.Type)
        {

            case "stream_event":

                foreach (ChatResponseUpdate update in ProjectStreamEvent(frame.Event, state))
                {
                    yield return update;
                }

                break;

            case "result":

                Complete(frame, state);

                break;

            default:

                // system/init, rate_limit_event, assistant, post_turn_summary and anything a future
                // release adds: not an answer, and not an error. Skipping unknown frames is what
                // keeps a CLI upgrade from breaking a turn.
                break;

        }

    }

    private IEnumerable<ChatResponseUpdate> ProjectStreamEvent(
        ClaudeCodeStreamEvent? streamEvent,
        FamiliarTurnState state)
    {

        if (streamEvent?.Type != "content_block_delta" || streamEvent.Delta is not { } delta)
        {
            yield break;
        }

        switch (delta.Type)
        {

            case "text_delta" when delta.Text is { Length: > 0 } text:

                state.EmittedText = true;

                yield return Update([new TextContent(text)]);

                break;

            case "thinking_delta" when delta.Thinking is { Length: > 0 } thinking:

                yield return Update([new TextReasoningContent(thinking)]);

                break;

            default:

                break;

        }

    }

    private void Complete(ClaudeCodeFrame frame, FamiliarTurnState state)
    {

        // `is_error`, not `subtype`. A rejected model still arrives as subtype "success", so reading
        // the subtype would turn an API error into a confident wrong answer.
        if (frame.IsError)
        {

            throw Refused(frame.Result);

        }

        state.Completed = true;

        state.CompleteText = frame.Result;

        state.Usage = MapUsage(frame.Usage);

    }

    private static UsageDetails? MapUsage(ClaudeCodeUsage? usage)
    {

        if (usage is null)
        {
            return null;
        }

        // Anthropic reports prompt tokens in three disjoint buckets: `input_tokens` counts only the
        // uncached remainder, while cache reads and cache writes are billed separately. Mapping
        // `input_tokens` straight onto UsageDetails.InputTokenCount — which downstream accounting
        // treats as the whole prompt, cache inclusive — would lose every cached token. The recorded
        // fixture makes that concrete: 2 input, 2,640 cache-creation, and a real cost of $0.0165.
        long uncachedInput = usage.InputTokens ?? 0L;

        long cacheRead = usage.CacheReadInputTokens ?? 0L;

        long cacheWritten = usage.CacheCreationInputTokens ?? 0L;

        long output = usage.OutputTokens ?? 0L;

        long input = uncachedInput + cacheRead + cacheWritten;

        if (input == 0L && output == 0L)
        {

            // "No usage reported" is preserved as absent rather than flattened to zero — a zero
            // would read as a free turn instead of an unknown one.
            return null;

        }

        return new UsageDetails
        {

            InputTokenCount = input,

            OutputTokenCount = output,

            CachedInputTokenCount = cacheRead,

            TotalTokenCount = input + output,

        };

    }

    private static ClaudeCodeFrame? TryParse(string line)
    {

        try
        {

            return JsonSerializer.Deserialize(line, FamiliarWireJsonContext.Default.ClaudeCodeFrame);

        }
        catch (JsonException)
        {

            // A malformed line is one frame, not the turn. The terminal frame decides the outcome,
            // so dropping noise here cannot turn a failure into a success.
            return null;

        }

    }

}
