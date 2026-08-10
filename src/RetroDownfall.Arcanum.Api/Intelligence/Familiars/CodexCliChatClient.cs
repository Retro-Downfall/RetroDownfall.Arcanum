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

    /// <summary>
    /// Codex feature flags that carry a tool the vendor's own agent loop can call.
    /// </summary>
    /// <remarks>
    /// Codex exposes no <c>--tools</c> switch, so this list is how its loop is switched off. It is
    /// applied as <c>-c features.&lt;name&gt;=false</c> rather than <c>--disable &lt;name&gt;</c>:
    /// <c>--disable</c> hard-errors on a name the installed CLI does not recognise, so a flag renamed
    /// in a later release would break every turn, while an unknown <c>-c</c> override is ignored.
    /// That tolerance is also the reason this list is best-effort, and why
    /// <see cref="ProjectItem"/> independently refuses a turn that ran a tool regardless.
    /// </remarks>
    internal static readonly string[] SuppressedFeatures =
    [
        "shell_tool",
        "unified_exec",
        "shell_snapshot",
        "browser_use",
        "browser_use_external",
        "browser_use_full_cdp_access",
        "in_app_browser",
        "computer_use",
        "apps",
        "image_generation",
        "web_search",
        "standalone_web_search",
        "hooks",
    ];

    /// <summary>
    /// Completed item types that only exist because the vendor loop invoked a tool.
    /// </summary>
    private static readonly string[] VendorToolItemTypes =
    [
        "command_execution",
        "mcp_tool_call",
        "web_search",
        "file_change",
        "patch_apply",
        "browser_action",
        "computer_use",
    ];

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
        ];

        // Arcanum owns the tool loop. `--sandbox read-only` only constrains what a command may
        // write, not whether the model may run one, so the vendor tools are switched off explicitly.
        foreach (string feature in SuppressedFeatures)
        {

            arguments.Add("-c");

            arguments.Add($"features.{feature}=false");

        }

        // Read the prompt from stdin rather than argv, and say so explicitly: with a prompt
        // argument present, piped stdin would be appended as a separate <stdin> block instead.
        arguments.Add("-");

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

        // Feature suppression is best-effort against a CLI Arcanum does not version-pin, so the
        // guarantee lives here: a turn that reached a vendor tool ran code outside
        // WorkspacePathPolicy, outside the Ward gate, and outside the audit record. Its answer is
        // laundered output, not a completion, and returning it would be worse than failing.
        if (item?.Type is { Length: > 0 } itemType
            && VendorToolItemTypes.Contains(itemType, StringComparer.Ordinal))
        {

            throw Refused(
                $"the Codex CLI ran its own '{itemType}' tool, which Arcanum's tool loop does not "
                + "govern. Update the Codex CLI, or remove the CodexCli provider until its agent "
                + "loop can be disabled.");

        }

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

            // CreateNew, not WriteAllText: a plain write follows an existing symlink. The working
            // directory is owner-only and fresh per turn, so this can only ever be a fresh file —
            // failing instead of following anything already at that path keeps it that way.
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {

                using StreamWriter writer = new(stream);

                writer.Write(jsonSchema);

            }

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
