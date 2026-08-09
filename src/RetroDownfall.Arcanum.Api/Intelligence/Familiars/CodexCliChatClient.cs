using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Api.Intelligence.Familiars;

/// <summary>
/// Runs a turn through the operator's Codex CLI in non-interactive exec mode.
/// </summary>
/// <remarks>
/// Pinned against <c>codex-cli 0.147.0</c>. <c>codex exec --json</c> emits JSONL events; unlike
/// Claude Code it has no token-delta mode, so a completed <c>agent_message</c> item is the finest
/// granularity available and the turn streams as one block. Codex has no system-prompt flag either,
/// so instructions are folded into the prompt.
/// </remarks>
internal sealed class CodexCliChatClient(
    IFamiliarProcessRunner runner,
    ProviderSettings provider,
    string resolvedModel,
    IReadOnlyList<string> deniedEnvironmentVariables)
    : FamiliarChatClient(runner, provider, resolvedModel, deniedEnvironmentVariables)
{

    protected override FamiliarProcessRequest BuildRequest(FamiliarPrompt prompt, string? jsonSchema)
    {

        // A read-only sandbox rooted at this turn's private directory, not the operator's workspace
        // and not the shared temp root: this is a transport asked for a completion, Arcanum's own
        // tools are the only ones that should touch a campaign's files, and Codex reads AGENTS.md
        // and execpolicy `.rules` out of its root — which nobody else may be able to write.
        string workingDirectory = WorkingDirectory;

        List<string> arguments =
        [
            "exec",

            "--json",

            "--sandbox",
            "read-only",

            "--skip-git-repo-check",

            // The Grimoire is Arcanum's session store; the Familiar must not keep a second one.
            "--ephemeral",

            // Project execpolicy files come from the working root; ignoring them keeps a planted
            // `.rules` file from widening what the Familiar may do.
            "--ignore-rules",

            "-C",
            workingDirectory,

            "-m",
            ResolvedModel,

            // Read the prompt from stdin rather than argv, and say so explicitly: with a prompt
            // argument present, piped stdin would be appended as a separate <stdin> block instead.
            "-",
        ];

        // Codex takes its output schema as a file rather than inline. The private working directory
        // this turn already owns is the natural place for it — nobody else can read or replace it,
        // and it disappears with the lease.
        if (jsonSchema is { Length: > 0 } && TryWriteSchema(workingDirectory, jsonSchema, out string? schemaPath))
        {

            arguments.Insert(arguments.Count - 1, "--output-schema");

            arguments.Insert(arguments.Count - 1, schemaPath!);

        }

        string text = prompt.SystemPrompt is { Length: > 0 } systemPrompt
            ? $"{systemPrompt}\n\n{prompt.Text}"
            : prompt.Text;

        return new FamiliarProcessRequest
        {

            FileName = FamiliarProviders.ResolveCommand(Provider),

            Arguments = arguments,

            StandardInput = text,

            WorkingDirectory = workingDirectory,

        };

    }

    protected override IEnumerable<ChatResponseUpdate> ProjectFrame(string line, FamiliarTurnState state)
    {

        CodexFrame? frame = TryParse(line);

        if (frame is null)
        {
            yield break;
        }

        switch (frame.Type)
        {

            case "item.completed":

                foreach (ChatResponseUpdate update in ProjectItem(frame.Item, state))
                {
                    yield return update;
                }

                break;

            case "turn.completed":

                state.Completed = true;

                state.Usage = MapUsage(frame.Usage);

                break;

            case "turn.failed":

                throw Refused(frame.Error?.Message);

            case "error":

                // A top-level error frame precedes turn.failed and carries the same text. Recorded
                // as a diagnostic, never as an answer: a stream that ends here has failed, and the
                // CLI's own words are what the operator needs to see.
                state.Diagnostic ??= frame.Message;

                break;

            default:

                // thread.started, turn.started, item.started, and anything a future release adds.
                break;

        }

    }

    private IEnumerable<ChatResponseUpdate> ProjectItem(CodexItem? item, FamiliarTurnState state)
    {

        switch (item?.Type)
        {

            case "agent_message" when item.Text is { Length: > 0 } text:

                state.EmittedText = true;

                yield return Update([new TextContent(text)]);

                break;

            case "reasoning" when item.Text is { Length: > 0 } reasoning:

                yield return Update([new TextReasoningContent(reasoning)]);

                break;

            case "error":

                // Codex reports a recoverable complaint (an unknown model name, say) as a completed
                // error item and keeps going. Remembered as a diagnostic, not thrown and not treated
                // as text: turn.failed is the verdict, and a complaint is not an answer.
                state.Diagnostic ??= item.Message ?? item.Text;

                break;

            default:

                break;

        }

    }

    private static UsageDetails? MapUsage(CodexUsage? usage)
    {

        if (usage is null)
        {
            return null;
        }

        long input = usage.InputTokens ?? 0L;

        long output = usage.OutputTokens ?? 0L;

        long cached = usage.CachedInputTokens ?? 0L;

        long reasoning = usage.ReasoningOutputTokens ?? 0L;

        if (input == 0L && output == 0L && cached == 0L && reasoning == 0L)
        {

            // Absent rather than zero: an unreported turn is unknown, not free.
            return null;

        }

        return new UsageDetails
        {

            InputTokenCount = input,

            OutputTokenCount = output,

            CachedInputTokenCount = cached,

            ReasoningTokenCount = reasoning,

            TotalTokenCount = input + output,

        };

    }

    private static bool TryWriteSchema(string workingDirectory, string jsonSchema, out string? path)
    {

        path = Path.Combine(workingDirectory, "output-schema.json");

        try
        {

            File.WriteAllText(path, jsonSchema);

            return true;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            // A schema Arcanum could not hand over is worth losing, not the turn: structured-output
            // validation still runs on the answer, and a mismatch retries as it always did.
            path = null;

            return false;

        }

    }

    private static CodexFrame? TryParse(string line)
    {

        try
        {

            return JsonSerializer.Deserialize(line, FamiliarWireJsonContext.Default.CodexFrame);

        }
        catch (JsonException)
        {

            return null;

        }

    }

}
