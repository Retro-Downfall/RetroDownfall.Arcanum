using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Familiars;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// The adapters are the whole of the new transport: everything above <c>IChatClientFactory</c> —
/// the turn engine, streaming projections, Wards, Sanctum, accounting — is unchanged and only works
/// if a Familiar looks exactly like any other <see cref="IChatClient"/>. These facts pin that
/// translation in both directions against output recorded from the real CLIs.
/// </summary>
public sealed class FamiliarChatClientTests
{

    private static readonly ProviderSettings ClaudeProvider = new()
    {
        Name = "ClaudeCode-subscription",
        Type = AiProviderKind.ClaudeCodeCli,
    };

    private static readonly ProviderSettings CodexProvider = new()
    {
        Name = "Codex-subscription",
        Type = AiProviderKind.CodexCli,
    };

    // ---- Claude Code -------------------------------------------------------------------------

    [Fact]
    public async Task Claude_buffered_turn_returns_the_assistant_text()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reply with exactly: PONG")],
            cancellationToken: CancellationToken.None);

        Assert.Equal("PONG", response.Text);

    }

    [Fact]
    public async Task Claude_buffered_turn_surfaces_provider_reported_usage()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        UsageDetails usage = Assert.IsType<UsageDetails>(response.Usage);

        // Anthropic's three prompt buckets are disjoint: the recorded turn is 2 uncached input plus
        // 2,640 written to cache, and InputTokenCount is the whole prompt.
        Assert.Equal(2642L, usage.InputTokenCount);

        Assert.Equal(5L, usage.OutputTokenCount);

        // Nothing was read back from cache on this turn.
        Assert.Equal(0L, usage.CachedInputTokenCount);

    }

    /// <summary>
    /// With <c>--include-partial-messages</c> the CLI emits real token deltas, so a Familiar streams
    /// like any other provider rather than delivering one block at the end.
    /// </summary>
    [Fact]
    public async Task Claude_streaming_turn_yields_text_deltas_as_they_arrive()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudePartialMessages);

        using IChatClient client = CreateClaude(runner, "claude-haiku");

        List<string> deltas = [];

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None))
        {

            foreach (AIContent content in update.Contents)
            {

                if (content is TextContent text && text.Text.Length > 0)
                {
                    deltas.Add(text.Text);
                }

            }

        }

        Assert.Equal("PONG PONG PONG", string.Concat(deltas));

    }

    [Fact]
    public async Task Claude_streaming_turn_projects_thinking_as_reasoning_content()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudePartialMessages);

        using IChatClient client = CreateClaude(runner, "claude-haiku");

        List<string> reasoning = [];

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None))
        {

            foreach (AIContent content in update.Contents)
            {

                if (content is TextReasoningContent thinking && thinking.Text.Length > 0)
                {
                    reasoning.Add(thinking.Text);
                }

            }

        }

        Assert.Contains("PONG PONG PONG", string.Concat(reasoning), StringComparison.Ordinal);

    }

    /// <summary>
    /// The non-partial stream carries no deltas at all — only whole assistant messages and a final
    /// result. Falling back to the result text is what keeps a plain <c>--output-format stream-json</c>
    /// run from completing with an empty answer.
    /// </summary>
    [Fact]
    public async Task Claude_streaming_turn_falls_back_to_the_result_frame_when_no_deltas_arrive()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        ChatResponse response = await client
            .GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None)
            .ToChatResponseAsync(CancellationToken.None);

        Assert.Equal("PONG", response.Text);

    }

    /// <summary>
    /// The CLI reports a rejected model with exit code 1 and <c>is_error</c> on the result frame,
    /// while still labelling the frame <c>subtype: "success"</c>. Keying off the subtype would turn
    /// an API error into a confident wrong answer.
    /// </summary>
    [Fact]
    public async Task Claude_reports_an_errored_result_frame_as_a_transport_failure()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeModelError);

        using IChatClient client = CreateClaude(runner, "definitely-not-a-real-model");

        FamiliarTransportException failure = await Assert.ThrowsAsync<FamiliarTransportException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None));

        Assert.Contains("definitely-not-a-real-model", failure.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Claude_never_completes_empty_when_the_familiar_says_nothing()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueLines();

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await Assert.ThrowsAsync<FamiliarTransportException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None));

    }

    [Fact]
    public async Task Claude_tolerates_a_malformed_frame_without_losing_the_turn()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueLines(
            "{ this is not json",
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"}");

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        Assert.Equal("ok", response.Text);

    }

    /// <summary>
    /// A truncated stream is not a short answer. Without a terminal result frame Arcanum cannot know
    /// the turn finished, so it must fail rather than hand back whatever arrived first.
    /// </summary>
    [Fact]
    public async Task Claude_fails_closed_on_a_stream_that_ends_without_a_result_frame()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueLines(
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"half an ans\"}}}");

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await Assert.ThrowsAsync<FamiliarTransportException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None));

    }

    [Fact]
    public async Task Claude_is_invoked_headlessly_with_its_own_agent_loop_disabled()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        IReadOnlyList<string> argv = runner.LastRequest.Arguments;

        Assert.Contains("--print", argv);

        Assert.Contains("--output-format", argv);

        Assert.Contains("stream-json", argv);

        Assert.Contains("--model", argv);

        Assert.Contains("claude-sonnet", argv);

        // Arcanum owns the tool loop. Delegating into the CLI's own agent loop is out of scope, so
        // its tools, its slash commands, and its MCP servers are all switched off.
        Assert.Contains("--tools", argv);

        Assert.Contains("--disable-slash-commands", argv);

        Assert.Contains("--strict-mcp-config", argv);

    }

    [Fact]
    public async Task Claude_receives_the_system_message_as_a_system_prompt()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "You are terse."),
                new ChatMessage(ChatRole.User, "hi"),
            ],
            cancellationToken: CancellationToken.None);

        IReadOnlyList<string> argv = runner.LastRequest.Arguments;

        int index = argv.ToList().IndexOf("--system-prompt");

        Assert.True(index >= 0);

        Assert.Equal("You are terse.", argv[index + 1]);

    }

    [Fact]
    public async Task The_prompt_travels_on_standard_input_not_on_the_command_line()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "the quick brown fox")],
            cancellationToken: CancellationToken.None);

        Assert.Equal("the quick brown fox", runner.LastRequest.StandardInput);

        Assert.DoesNotContain("the quick brown fox", runner.LastRequest.Arguments);

    }

    /// <summary>
    /// A composed Arcanum system prompt carries attached-file bodies and resonant spell text, so it
    /// routinely runs past the OS argument ceilings — 32,767 characters for a whole Windows command
    /// line, 128 KiB for a single Linux argument. Putting it on argv makes <c>Process.Start</c> fail
    /// and the turn falls back to another provider with "check that it is executable", so an
    /// oversized prompt travels on stdin, which has no such limit.
    /// </summary>
    [Fact]
    public async Task Claude_folds_an_oversized_system_prompt_into_standard_input()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        string systemPrompt = "ATTACHED FILE BODY " + new string('x', 200 * 1024);

        _ = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, "hi"),
            ],
            cancellationToken: CancellationToken.None);

        FamiliarProcessRequest request = runner.LastRequest;

        Assert.DoesNotContain("--system-prompt", request.Arguments);

        Assert.All(request.Arguments, static argument => Assert.True(argument.Length <= 8192));

        Assert.True(request.Arguments.Sum(static argument => argument.Length + 1) < 16_384);

        Assert.Contains("ATTACHED FILE BODY", request.StandardInput, StringComparison.Ordinal);

        Assert.Contains("hi", request.StandardInput, StringComparison.Ordinal);

    }

    /// <summary>
    /// The same ceiling applies to <c>--json-schema</c>. Arcanum validates structured output and
    /// retries a mismatch, so losing the hint costs a retry; losing the spawn costs the turn.
    /// </summary>
    [Fact]
    public async Task Claude_keeps_an_oversized_output_schema_off_the_command_line()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ResponseFormat = OversizedSchemaFormat() },
            CancellationToken.None);

        Assert.All(
            runner.LastRequest.Arguments,
            static argument => Assert.True(argument.Length <= 8192));

    }

    /// <summary>
    /// A one-shot process has no session to resume, so the earlier turns have to travel in the
    /// prompt. Role labels appear only when there is more than one message to disambiguate.
    /// </summary>
    [Fact]
    public async Task A_multi_turn_conversation_is_rendered_as_a_labelled_transcript()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "first question"),
                new ChatMessage(ChatRole.Assistant, "first answer"),
                new ChatMessage(ChatRole.User, "second question"),
            ],
            cancellationToken: CancellationToken.None);

        string prompt = runner.LastRequest.StandardInput!;

        Assert.Contains("first question", prompt, StringComparison.Ordinal);

        Assert.Contains("first answer", prompt, StringComparison.Ordinal);

        Assert.Contains("second question", prompt, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_single_user_message_is_sent_verbatim_without_role_labels()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "just this")],
            cancellationToken: CancellationToken.None);

        Assert.Equal("just this", runner.LastRequest.StandardInput);

    }

    // ---- Codex -------------------------------------------------------------------------------

    [Fact]
    public async Task Codex_buffered_turn_returns_the_agent_message()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexSuccess);

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reply with exactly: PONG")],
            cancellationToken: CancellationToken.None);

        Assert.Equal("PONG", response.Text);

    }

    [Fact]
    public async Task Codex_surfaces_provider_reported_usage_including_cached_and_reasoning_tokens()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexSuccess);

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        UsageDetails usage = Assert.IsType<UsageDetails>(response.Usage);

        Assert.Equal(16318L, usage.InputTokenCount);

        Assert.Equal(9984L, usage.CachedInputTokenCount);

        Assert.Equal(265L, usage.OutputTokenCount);

        Assert.Equal(257L, usage.ReasoningTokenCount);

    }

    [Fact]
    public async Task Codex_reports_a_failed_turn_as_a_transport_failure()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexTurnFailed);

        using IChatClient client = CreateCodex(runner, "definitely-not-a-real-model");

        FamiliarTransportException failure = await Assert.ThrowsAsync<FamiliarTransportException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None));

        Assert.Contains("not supported", failure.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Codex_is_invoked_non_interactively_with_a_read_only_sandbox()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexSuccess);

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        IReadOnlyList<string> argv = runner.LastRequest.Arguments;

        Assert.Equal("exec", argv[0]);

        Assert.Contains("--json", argv);

        Assert.Contains("--sandbox", argv);

        Assert.Contains("read-only", argv);

        Assert.Contains("--skip-git-repo-check", argv);

        // Arcanum owns session state in the Grimoire; the Familiar must not also persist one.
        Assert.Contains("--ephemeral", argv);

        Assert.Contains("-m", argv);

        Assert.Contains("gpt-5.6", argv);

        // `-` makes the prompt come from stdin explicitly rather than being appended to it.
        Assert.Equal("-", argv[^1]);

    }

    /// <summary>
    /// Codex has no <c>--tools</c> switch, so the CLI's own agent loop is suppressed through feature
    /// overrides instead. They go through <c>-c features.&lt;name&gt;=false</c> rather than
    /// <c>--disable &lt;name&gt;</c> deliberately: <c>--disable</c> rejects a name the installed CLI
    /// does not know, so a renamed flag in a later release would fail every turn, while an unknown
    /// <c>-c</c> override is ignored.
    /// </summary>
    [Fact]
    public async Task Codex_disables_the_vendor_agent_loop_through_tolerant_config_overrides()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexSuccess);

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        IReadOnlyList<string> argv = runner.LastRequest.Arguments;

        Assert.DoesNotContain("--disable", argv);

        foreach (string feature in CodexCliChatClient.SuppressedFeatures)
        {

            Assert.Contains($"features.{feature}=false", argv);

        }

        Assert.Contains("shell_tool", string.Join(' ', argv));

    }

    /// <summary>
    /// Codex reads execpolicy <c>.rules</c> files out of its working root, and they widen what the
    /// CLI may run. Ignoring them is what keeps a planted rules file from steering a turn — the
    /// Codex counterpart to Claude Code's <c>--setting-sources user</c>.
    /// </summary>
    [Fact]
    public async Task Codex_ignores_repository_execpolicy_rules()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexSuccess);

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        Assert.Contains("--ignore-rules", runner.LastRequest.Arguments);

    }

    /// <summary>
    /// Defence in depth for the above. Feature suppression is best-effort against a CLI Arcanum does
    /// not version-pin, so the projection refuses a turn that ran a vendor tool anyway. Returning the
    /// agent message would launder output produced by a shell command that escaped
    /// <c>WorkspacePathPolicy</c> and the Ward gate entirely.
    /// </summary>
    [Fact]
    public async Task Codex_fails_closed_when_the_vendor_loop_executed_a_tool_anyway()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexShellTool);

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        FamiliarTransportException failure = await Assert.ThrowsAsync<FamiliarTransportException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "who am i")],
                cancellationToken: CancellationToken.None));

        Assert.Contains("command_execution", failure.Message, StringComparison.Ordinal);

        // The laundered answer must not reach the caller in any form.
        Assert.DoesNotContain("logged in as", failure.Message, StringComparison.OrdinalIgnoreCase);

    }

    // ---- Shared ------------------------------------------------------------------------------

    /// <summary>
    /// A missing binary reaches the adapter as a runner failure, and must arrive at the turn engine
    /// as a transport failure carrying the remediation — never as a silent empty completion.
    /// </summary>
    [Theory]
    [InlineData(AiProviderKind.ClaudeCodeCli)]
    [InlineData(AiProviderKind.CodexCli)]
    public async Task A_missing_binary_fails_closed_with_the_runners_remediation(AiProviderKind kind)
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFailure(
            new FamiliarProcessException(
                FamiliarProcessFailure.NotInstalled,
                "'claude' was not found."));

        using IChatClient client = kind == AiProviderKind.ClaudeCodeCli
            ? CreateClaude(runner, "claude-sonnet")
            : CreateCodex(runner, "gpt-5.6");

        FamiliarTransportException failure = await Assert.ThrowsAsync<FamiliarTransportException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None));

        Assert.Equal(FamiliarProcessFailure.NotInstalled, failure.Failure);

        Assert.Contains("was not found", failure.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// Arcanum's configured provider keys must not travel to a Familiar, so the adapter names them
    /// for the runner to strip. The Familiar's own vendor credentials are the operator's business.
    /// </summary>
    [Fact]
    public async Task Configured_provider_credential_variables_are_named_for_stripping()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "compat",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1",
                    CredentialEnvironmentVariable = "MY_OPENAI_KEY",
                    Models = ["gpt-4o"],
                },
                ClaudeProvider,
            ],
        };

        using IChatClient client = new ClaudeCodeCliChatClient(
            runner,
            ClaudeProvider,
            "claude-sonnet",
            FamiliarEnvironmentDenyList.Build(settings));

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        Assert.Contains("MY_OPENAI_KEY", runner.LastRequest.DeniedEnvironmentVariables);

        // The derived default is stripped too — an operator who never named one still has a key.
        Assert.Contains("ARCANUM_PROVIDER_COMPAT_API_KEY", runner.LastRequest.DeniedEnvironmentVariables);

    }

    // ---- Defects the review caught -------------------------------------------------------------

    /// <summary>
    /// Both CLIs read project state out of their working root — Claude Code's
    /// <c>.claude/settings.json</c> hooks run shell commands, Codex reads <c>AGENTS.md</c> — so
    /// inheriting the host's directory would let whatever repository Arcanum was started in execute
    /// code on every turn. It must be a private directory nobody else can write.
    /// </summary>
    [Theory]
    [InlineData(AiProviderKind.ClaudeCodeCli)]
    [InlineData(AiProviderKind.CodexCli)]
    public async Task A_familiar_never_runs_in_the_hosts_current_directory(AiProviderKind kind)
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(
            kind == AiProviderKind.ClaudeCodeCli
                ? FamiliarFixtures.ClaudeSuccess
                : FamiliarFixtures.CodexSuccess);

        using IChatClient client = kind == AiProviderKind.ClaudeCodeCli
            ? CreateClaude(runner, "claude-sonnet")
            : CreateCodex(runner, "gpt-5.6");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        string working = runner.LastRequest.WorkingDirectory!;

        Assert.False(string.IsNullOrWhiteSpace(working));

        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(System.Environment.CurrentDirectory),
            Path.TrimEndingDirectorySeparator(working));

        // Not the shared temp root either: on Linux that is world-writable, so any local account
        // could plant the very files this is protecting against.
        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            Path.TrimEndingDirectorySeparator(working));

    }

    /// <summary>
    /// Project and local settings come from the working directory and can carry a <c>hooks</c> block
    /// that runs shell commands. Restricting the CLI to user settings is what keeps a repository from
    /// executing code merely because Arcanum spawned a Familiar near it.
    /// </summary>
    [Fact]
    public async Task Claude_loads_user_settings_only_so_a_repository_cannot_run_hooks()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        List<string> argv = [.. runner.LastRequest.Arguments];

        int index = argv.IndexOf("--setting-sources");

        Assert.True(index >= 0);

        Assert.Equal("user", argv[index + 1]);

    }

    /// <summary>
    /// Anthropic reports prompt tokens in three disjoint buckets. Reading only <c>input_tokens</c>
    /// would drop cache reads and cache writes — the recorded turn is 2 uncached against 2,640
    /// cache-creation tokens, so the loss is the whole prompt.
    /// </summary>
    [Fact]
    public async Task Claude_usage_counts_cache_written_and_cache_read_prompt_tokens()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        UsageDetails usage = Assert.IsType<UsageDetails>(response.Usage);

        // 2 uncached + 2,640 written to cache + 0 read from cache.
        Assert.Equal(2642L, usage.InputTokenCount);

    }

    /// <summary>
    /// A terminal frame with no answer is a completed turn with nothing in it. Left alone it reaches
    /// the operator as an empty assistant message — the same silent failure wearing a success label.
    /// </summary>
    [Fact]
    public async Task A_terminal_frame_with_no_answer_fails_closed()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueLines(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"\"}");

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        FamiliarTransportException failure = await Assert.ThrowsAsync<FamiliarTransportException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None));

        Assert.Contains("without an answer", failure.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// Codex reports a recoverable complaint as a completed <c>error</c> item and keeps going. It is
    /// a diagnostic, not the assistant's answer — handing it back as text would present an internal
    /// warning to the operator as the model's reply.
    /// </summary>
    [Fact]
    public async Task A_codex_error_item_never_becomes_the_assistants_answer()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueLines(
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"error\",\"message\":\"Model metadata not found.\"}}",
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        FamiliarTransportException failure = await Assert.ThrowsAsync<FamiliarTransportException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None));

        // The CLI's own words explain the empty turn, rather than standing in for the answer.
        Assert.Contains("Model metadata not found.", failure.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// Arcanum validates structured output and retries a mismatch, so dropping a requested schema
    /// would spend turns getting prose rejected. Both CLIs can be told the shape.
    /// </summary>
    [Fact]
    public async Task Claude_passes_a_requested_output_schema_to_the_cli()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.ClaudeSuccess);

        using IChatClient client = CreateClaude(runner, "claude-sonnet");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ResponseFormat = SchemaFormat() },
            CancellationToken.None);

        List<string> argv = [.. runner.LastRequest.Arguments];

        int index = argv.IndexOf("--json-schema");

        Assert.True(index >= 0);

        Assert.Contains("\"answer\"", argv[index + 1], StringComparison.Ordinal);

    }

    [Fact]
    public async Task Codex_passes_a_requested_output_schema_as_a_file_in_its_private_directory()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexSuccess);

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ResponseFormat = SchemaFormat() },
            CancellationToken.None);

        List<string> argv = [.. runner.LastRequest.Arguments];

        int index = argv.IndexOf("--output-schema");

        Assert.True(index >= 0);

        Assert.StartsWith(runner.LastRequest.WorkingDirectory!, argv[index + 1], StringComparison.Ordinal);

        Assert.Contains("\"answer\"", File.ReadAllText(argv[index + 1]), StringComparison.Ordinal);

    }

    /// <summary>
    /// One <see cref="IChatClient"/> serves every model call in a turn, and the structured-output
    /// correction loop re-invokes that same instance with the same <c>ResponseFormat</c>. The schema
    /// file therefore has to be per call: a fixed name opened <c>CreateNew</c> throws on the second
    /// write, and the swallowed failure silently drops <c>--output-schema</c> on exactly the retry
    /// that exists to make the answer well-formed.
    /// </summary>
    [Fact]
    public async Task Codex_writes_a_fresh_output_schema_for_every_call_on_one_client()
    {

        RecordingFamiliarProcessRunner runner = new();

        runner.EnqueueFixture(FamiliarFixtures.CodexSuccess);

        runner.EnqueueFixture(FamiliarFixtures.CodexSuccess);

        using IChatClient client = CreateCodex(runner, "gpt-5.6");

        ChatOptions options = new() { ResponseFormat = SchemaFormat() };

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            options,
            CancellationToken.None);

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "that was not valid JSON; try again")],
            options,
            CancellationToken.None);

        Assert.Equal(2, runner.Requests.Count);

        List<string> first = [.. runner.Requests[0].Arguments];

        List<string> second = [.. runner.Requests[1].Arguments];

        int firstIndex = first.IndexOf("--output-schema");

        int secondIndex = second.IndexOf("--output-schema");

        Assert.True(firstIndex >= 0, "the first call lost --output-schema");

        Assert.True(secondIndex >= 0, "the correction call lost --output-schema");

        // Distinct paths, not a reused one: handing the child a name that already exists is the
        // symlink-following hazard `CreateNew` is there to refuse.
        Assert.NotEqual(first[firstIndex + 1], second[secondIndex + 1]);

        Assert.Contains(
            "\"answer\"",
            File.ReadAllText(second[secondIndex + 1]),
            StringComparison.Ordinal);

    }

    private static ChatResponseFormat SchemaFormat() =>
        ChatResponseFormat.ForJsonSchema(
            JsonSerializer.Deserialize<JsonElement>(
                "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}}}"),
            "answer",
            schemaDescription: string.Empty);

    private static ChatResponseFormat OversizedSchemaFormat() =>
        ChatResponseFormat.ForJsonSchema(
            JsonSerializer.Deserialize<JsonElement>(
                "{\"type\":\"object\",\"description\":\""
                + new string('d', 64 * 1024)
                + "\",\"properties\":{\"answer\":{\"type\":\"string\"}}}"),
            "answer",
            schemaDescription: string.Empty);

    private static IChatClient CreateClaude(IFamiliarProcessRunner runner, string model) =>
        new ClaudeCodeCliChatClient(runner, ClaudeProvider, model, []);

    private static IChatClient CreateCodex(IFamiliarProcessRunner runner, string model) =>
        new CodexCliChatClient(runner, CodexProvider, model, []);

}
