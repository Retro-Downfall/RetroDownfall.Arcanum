using System.Collections.ObjectModel;

using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class ShellCommandParserTests
{
    private readonly ShellCommandParser _parser = new();

    /// <summary>
    /// Pins get their own verbs because <c>/context</c> is now the Claude-aligned context-window
    /// view rather than a pin manager.
    /// </summary>
    [Theory]
    [InlineData("/pins", (int)ShellCommandKind.PinList)]
    [InlineData("/unpin 11111111-1111-1111-1111-111111111111", (int)ShellCommandKind.Unpin)]
    [InlineData("/context", (int)ShellCommandKind.Context)]
    public void Parses_context_management_commands(string input, int expected)
    {
        Assert.Equal((ShellCommandKind)expected, _parser.Parse(input).Kind);
    }

    [Fact]
    public void Parses_context_pin_kind_and_target()
    {
        ParsedShellCommand parsed = _parser.Parse("/pin symbolRange src/App.cs:10-20");
        Assert.Equal(ShellCommandKind.Pin, parsed.Kind);
        Assert.Equal("symbolRange", parsed.Argument);
        Assert.Equal("src/App.cs:10-20", parsed.SecondaryArgument);
    }

    [Theory]
    [InlineData("/help", "Help")]
    [InlineData("/exit", "Exit")]
    [InlineData("/quit", "Quit")]
    [InlineData("/clear", "Clear")]
    [InlineData("/compact", "Compact")]
    [InlineData("/config", "Config")]
    [InlineData("/cost", "Cost")]
    [InlineData("/memory", "Memory")]
    [InlineData("/look", "Look")]
    [InlineData("/status", "Status")]
    [InlineData("/doctor", "Doctor")]
    [InlineData("/mcp", "Mcp")]
    [InlineData("/mcp reload", "McpReload")]
    [InlineData("/arsenal", "Arsenal")]
    [InlineData("/tools", "Tools")]
    [InlineData("/context", "Context")]
    [InlineData("/keys", "Keys")]
    [InlineData("/model", "Model")]
    [InlineData("/model gpt-4o-mini", "ModelSelect")]
    [InlineData("/provider list", "ProviderList")]
    [InlineData("/campaign list", "CampaignList")]
    [InlineData("/session list", "SessionList")]
    [InlineData("/spell list", "SpellList")]
    [InlineData("/ward list", "WardList")]
    [InlineData("/ward allow", "WardAllow")]
    [InlineData("/ward deny", "WardDeny")]
    public void Parses_allowlisted_commands(string input, string expectedKind)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(Enum.Parse<ShellCommandKind>(expectedKind), parsed.Kind);
    }

    [Theory]
    [InlineData("/campaign list 50", "50")]
    [InlineData("/ward list 150", "150")]
    public void Paged_list_commands_capture_the_requested_offset(
        string input,
        string expectedOffset)
    {

        ParsedShellCommand parsed = _parser.Parse(input);

        Assert.Equal(expectedOffset, parsed.Argument);

    }

    [Fact]
    public void Spell_list_captures_an_opaque_cursor_without_interpreting_it()
    {

        ParsedShellCommand parsed = _parser.Parse(
            "/spell list AQID-_opaque-cursor");

        Assert.Equal(ShellCommandKind.SpellList, parsed.Kind);

        Assert.Equal("AQID-_opaque-cursor", parsed.Argument);

    }

    [Theory]
    [InlineData("/campaign list -1")]
    [InlineData("/ward list 1 2")]
    public void Paged_list_commands_reject_invalid_offsets(string input)
    {

        ParsedShellCommand parsed = _parser.Parse(input);

        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);

        Assert.Contains("offset", parsed.DenialMessage, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Spell_list_rejects_multiple_cursor_tokens_with_cursor_usage()
    {

        ParsedShellCommand parsed = _parser.Parse(
            "/spell list one two");

        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);

        Assert.Contains(
            "opaque-cursor",
            parsed.DenialMessage,
            StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Ward_allow_captures_optional_id()
    {
        ParsedShellCommand parsed = _parser.Parse("/ward allow abc-123");
        Assert.Equal(ShellCommandKind.WardAllow, parsed.Kind);
        Assert.Equal("abc-123", parsed.Argument);
    }

    [Fact]
    public void Session_resume_captures_id()
    {
        Guid id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        ParsedShellCommand parsed = _parser.Parse($"/resume {id:D}");
        Assert.Equal(ShellCommandKind.SessionResume, parsed.Kind);
        Assert.Equal(id.ToString("D"), parsed.Argument);
    }

    [Theory]
    [InlineData("/daemon")]
    [InlineData("/daemon jobs")]
    [InlineData("/daemon status")]
    public void Denies_all_daemon_commands(string input)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);
        Assert.Contains("Daemon", parsed.DenialMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/serve")]
    [InlineData("/key show")]
    [InlineData("/key set")]
    public void Denies_secrets_and_host_lifecycle(string input)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);
    }

    [Fact]
    public void Attach_is_allowlisted()
    {
        ParsedShellCommand parsed = _parser.Parse("/attach foo.png");
        Assert.Equal(ShellCommandKind.Attach, parsed.Kind);
        Assert.Equal("foo.png", parsed.Argument);
    }
}

public sealed class SessionLogBufferTests
{
    [Fact]
    public void Drops_oldest_when_over_max_entries()
    {
        SessionLogBuffer buffer = new(maxEntries: 3);
        buffer.Append(SessionLogEntryKind.Status, "a");
        buffer.Append(SessionLogEntryKind.Status, "b");
        buffer.Append(SessionLogEntryKind.Status, "c");
        buffer.Append(SessionLogEntryKind.Status, "d");

        Assert.Equal(3, buffer.Count);
        IReadOnlyList<SessionLogEntry> snap = buffer.Snapshot();
        Assert.Equal("b", snap[0].Text);
        Assert.Equal("d", snap[^1].Text);
    }

    [Fact]
    public void Truncates_command_output_with_marker()
    {
        SessionLogBuffer buffer = new(maxCommandChars: 80);
        string longText = new('x', 200);
        SessionLogEntry entry = buffer.Append(SessionLogEntryKind.Command, longText);

        Assert.Contains(SessionLogBuffer.TruncationMarker.Trim(), entry.Text, StringComparison.Ordinal);
        Assert.True(entry.Text.Length <= 80);
        Assert.True(entry.Text.Length < longText.Length);
    }

    [Fact]
    public void WrapLine_breaks_on_spaces_and_hard_breaks_long_tokens()
    {
        string[] soft = SessionLogBuffer.WrapLine("hello world friends", 10).ToArray();
        Assert.Equal(["hello", "world", "friends"], soft);

        string[] hard = SessionLogBuffer.WrapLine("abcdefghijklmnop", 5).ToArray();
        Assert.Equal(["abcde", "fghij", "klmno", "p"], hard);
    }

    [Fact]
    public void CopyLinesTo_soft_wraps_to_width()
    {
        SessionLogBuffer buffer = new();
        buffer.Append(SessionLogEntryKind.Assistant, "one two three four five six");

        ObservableCollection<string> lines = new();
        buffer.CopyLinesTo(lines, wrapWidth: 14);

        Assert.All(lines, line => Assert.True(line.Length <= 14, line));
        Assert.Contains(lines, static l => l.Contains("Mage:", StringComparison.Ordinal));
        Assert.True(lines.Count >= 2);
    }

    [Fact]
    public void CopyLinesTo_collapses_consecutive_blank_lines_to_one()
    {
        SessionLogBuffer buffer = new();
        buffer.Append(SessionLogEntryKind.Assistant, "alpha\n\n\n\nbeta\n\n\ngamma");

        ObservableCollection<string> lines = new();
        buffer.CopyLinesTo(lines, wrapWidth: 80);

        string joined = string.Join('\n', lines);
        Assert.DoesNotContain("\n\n\n", joined, StringComparison.Ordinal);
        Assert.Contains("alpha\n\nbeta\n\ngamma", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyLinesTo_inserts_single_blank_between_entries()
    {
        SessionLogBuffer buffer = new();
        buffer.Append(SessionLogEntryKind.User, "hi");
        buffer.Append(SessionLogEntryKind.Assistant, "hello");

        ObservableCollection<string> lines = new();
        buffer.CopyLinesTo(lines, wrapWidth: 80);

        Assert.Equal(3, lines.Count);
        Assert.Contains("Dungeon Master:", lines[0], StringComparison.Ordinal);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Contains("Mage:", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveEphemeralGeneratingStatuses_clears_stuck_placeholder()
    {
        SessionLogBuffer buffer = new();
        buffer.Append(SessionLogEntryKind.User, "hi");
        buffer.Append(SessionLogEntryKind.Status, SessionLogBuffer.GeneratingStatusMessage);
        buffer.Append(SessionLogEntryKind.Assistant, "hello");

        Assert.Equal(1, buffer.RemoveEphemeralGeneratingStatuses());
        Assert.DoesNotContain(
            buffer.Snapshot(),
            static e => SessionLogBuffer.IsEphemeralGeneratingStatus(e.Text));
    }

    [Fact]
    public void Reasoning_entry_is_distinct_in_memory_and_can_precede_streaming_answer()
    {
        SessionLogBuffer buffer = new(maxAssistantChars: 256);
        buffer.Append(SessionLogEntryKind.User, "question");
        SessionLogEntry assistant = buffer.Append(
            SessionLogEntryKind.Assistant,
            string.Empty,
            streaming: true);

        SessionLogEntry reasoning = buffer.InsertBefore(
            assistant,
            SessionLogEntryKind.Reasoning,
            "thinking",
            streaming: true);
        buffer.UpdateStreaming(reasoning, "thinking more");
        buffer.CompleteStreaming(reasoning);
        buffer.CompleteStreaming(assistant, "answer");

        IReadOnlyList<SessionLogEntry> snapshot = buffer.Snapshot();
        Assert.Equal(
            [
                SessionLogEntryKind.User,
                SessionLogEntryKind.Reasoning,
                SessionLogEntryKind.Assistant,
            ],
            snapshot.Select(static entry => entry.Kind));
        Assert.False(reasoning.Streaming);
        Assert.Equal("answer", assistant.Text);

        ObservableCollection<string> lines = [];
        buffer.CopyLinesTo(lines, wrapWidth: 80);
        string rendered = string.Join('\n', lines);
        Assert.Contains("Reasoning (ephemeral):", rendered, StringComparison.Ordinal);
        Assert.Contains("thinking more", rendered, StringComparison.Ordinal);
        Assert.Contains("Mage: answer", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceWithHistory_drops_reasoning_entries()
    {
        SessionLogBuffer buffer = new();

        buffer.ReplaceWithHistory(
            [
                (SessionLogEntryKind.User, "question"),
                (SessionLogEntryKind.Reasoning, "must remain ephemeral"),
                (SessionLogEntryKind.Assistant, "answer"),
            ],
            showOlderMessagesMarker: false);

        Assert.DoesNotContain(
            buffer.Snapshot(),
            static entry => entry.Kind == SessionLogEntryKind.Reasoning);
        Assert.DoesNotContain(
            "must remain ephemeral",
            buffer.RenderPlainText(),
            StringComparison.Ordinal);
    }
}

public sealed class SessionIdLineParserTests
{
    [Fact]
    public void Extracts_guid_from_dedicated_or_inline_line()
    {
        Guid id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.True(SessionIdLineParser.TryExtract(id.ToString("D"), out Guid a));
        Assert.Equal(id, a);

        Assert.True(SessionIdLineParser.TryExtract($"- {id:D}  title", out Guid b));
        Assert.Equal(id, b);
    }

    [Fact]
    public void Extracts_from_title_line_by_looking_at_previous()
    {
        Guid id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        string[] lines = [id.ToString("D"), "  (untitled)"];
        Assert.True(SessionIdLineParser.TryExtractNear(lines, index: 1, out Guid found));
        Assert.Equal(id, found);
    }
}

public sealed class CommandCenterBrandBannerTests
{
    [Fact]
    public void Banner_is_three_square_rows_spelling_arcanum_width()
    {
        Assert.Equal(3, CommandCenterBrandBanner.Lines.Length);
        Assert.Equal(CommandCenterBrandBanner.RowCount, CommandCenterBrandBanner.Lines.Length);
        Assert.True(CommandCenterBrandBanner.Width >= 20);
        Assert.Contains("█", CommandCenterBrandBanner.AsText(), StringComparison.Ordinal);
        Assert.Contains("Retro Downfall", CommandCenterBrandBanner.RightsBlurb, StringComparison.Ordinal);
        Assert.Contains("All rights reserved", CommandCenterBrandBanner.RightsBlurb, StringComparison.Ordinal);
        Assert.Contains(
            DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CommandCenterBrandBanner.RightsBlurb,
            StringComparison.Ordinal);
        Assert.Equal(4, CommandCenterBrandBanner.BrandedContentRows);
        Assert.True(CommandCenterBrandBanner.Fits(80, 24));
        Assert.False(CommandCenterBrandBanner.Fits(20, 10));
    }
}

public sealed class CommandCenterAppSizeGateTests
{
    [Theory]
    [InlineData(80, 12, true)]
    [InlineData(79, 12, false)]
    [InlineData(80, 11, false)]
    [InlineData(120, 40, true)]
    public void Viewport_floor(int cols, int rows, bool expected) =>
        Assert.Equal(expected, CommandCenterApp.IsViewportLargeEnough(cols, rows));
}

public sealed class ShellCommandDispatcherTests
{
    [Fact]
    public async Task Spell_list_requests_one_server_page_and_prints_exact_opaque_continuation()
    {

        RecordingSpellCatalogHandler handler = new();

        ShellCommandDispatcher dispatcher = CreateDispatcher(handler);

        CommandCenterState state = new(new SessionLogBuffer())
        {

            WorkingDirectory = "/workspace root",

        };

        _ = await dispatcher.DispatchAsync(
            "/spell list opaque-prior",
            state,
            CancellationToken.None);

        Uri requestUri = Assert.Single(handler.Requests).RequestUri!;

        Assert.Equal("/api/spells", requestUri.AbsolutePath);

        Assert.Contains("paged=true", requestUri.Query, StringComparison.Ordinal);

        Assert.Contains(
            "cursor=opaque-prior",
            requestUri.Query,
            StringComparison.Ordinal);

        string rendered = state.Log.RenderPlainText();

        Assert.Contains("- catalog-spell", rendered, StringComparison.Ordinal);

        Assert.Contains(
            "/spell list opaque-next",
            rendered,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Display_page_reports_physical_owner_saved_state_and_exact_continuation()
    {

        string rendered = ShellCommandDispatcher.AppendDisplayContinuation(
            "- item",
            "spell",
            offset: 50,
            shown: 50,
            total: 125,
            hasMore: true);

        Assert.Contains("Physical terminal-rendering boundary", rendered, StringComparison.Ordinal);

        Assert.Contains("100 of 125", rendered, StringComparison.Ordinal);

        Assert.Contains("Server state was not changed", rendered, StringComparison.Ordinal);

        Assert.Contains("/spell list 100", rendered, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Session_resume_invalid_guid_errors_without_binding()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        CommandCenterState state = new(new SessionLogBuffer());

        ShellDispatchResult result = await dispatcher.DispatchAsync(
            "/resume not-a-guid",
            state,
            CancellationToken.None);

        Assert.Equal(ShellDispatchResult.Continue, result);
        Assert.Null(state.SessionId);
        Assert.Contains("Usage:", state.Log.RenderPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_resume_failed_api_keeps_prior_session()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        Guid prior = Guid.Parse("22222222-2222-2222-2222-222222222222");
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(prior, "Prior", "Active", 1);
        state.Log.Append(SessionLogEntryKind.User, "keep");

        ShellDispatchResult result = await dispatcher.DispatchAsync(
            "/resume 33333333-3333-3333-3333-333333333333",
            state,
            CancellationToken.None);

        Assert.Equal(ShellDispatchResult.Continue, result);
        Assert.Equal(prior, state.SessionId);
        Assert.Contains("keep", state.Log.RenderPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryBeginTurn_is_single_winner()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Assert.True(state.TryBeginTurn());
        Assert.True(state.Generating);
        Assert.False(state.TryBeginTurn());
        state.EndTurn();
        Assert.False(state.Generating);
        Assert.True(state.TryBeginTurn());
    }

    [Fact]
    public async Task Session_new_clears_id()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        CommandCenterState state = new(new SessionLogBuffer()) { SessionId = Guid.NewGuid() };

        _ = await dispatcher.DispatchAsync("/clear", state, CancellationToken.None);

        Assert.Null(state.SessionId);
    }

    [Fact]
    public async Task Session_new_while_generating_is_denied_without_mutation()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        Guid prior = Guid.Parse("44444444-4444-4444-4444-444444444444");
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(prior, "Busy", "Active", 1);
        Assert.True(state.TryBeginTurn());
        state.Log.Append(SessionLogEntryKind.User, "in-flight");

        _ = await dispatcher.DispatchAsync("/clear", state, CancellationToken.None);

        Assert.Equal(prior, state.SessionId);
        Assert.Equal("Busy", state.SessionTitle);
        Assert.Contains("in-flight", state.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.Equal(CommandCenterSessionMutationGuard.GeneratingDenyMessage, state.FooterHint);
    }

    [Fact]
    public async Task Session_resume_while_generating_is_denied_without_mutation()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        Guid prior = Guid.Parse("55555555-5555-5555-5555-555555555555");
        Guid target = Guid.Parse("66666666-6666-6666-6666-666666666666");
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(prior, "Busy", "Active", 1);
        Assert.True(state.TryBeginTurn());
        state.Log.Append(SessionLogEntryKind.User, "keep");

        _ = await dispatcher.DispatchAsync($"/resume {target:D}", state, CancellationToken.None);

        Assert.Equal(prior, state.SessionId);
        Assert.Contains("keep", state.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.Equal(CommandCenterSessionMutationGuard.GeneratingDenyMessage, state.FooterHint);
    }

    [Fact]
    public async Task Exit_sets_request_exit()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        CommandCenterState state = new(new SessionLogBuffer());

        ShellDispatchResult result = await dispatcher.DispatchAsync("/exit", state, CancellationToken.None);

        Assert.Equal(ShellDispatchResult.Exit, result);
        Assert.True(state.RequestExit);
        Assert.Equal(0, state.ExitCode);
    }

    [Fact]
    public async Task Deny_serve_appends_error()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        CommandCenterState state = new(new SessionLogBuffer());

        _ = await dispatcher.DispatchAsync("/serve", state, CancellationToken.None);

        Assert.Contains("not available", state.Log.RenderPlainText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compact_doctor_points_outside()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        CommandCenterState state = new(new SessionLogBuffer());

        _ = await dispatcher.DispatchAsync("/doctor", state, CancellationToken.None);

        string text = state.Log.RenderPlainText();
        Assert.Contains("compact", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arcanum doctor", text, StringComparison.Ordinal);
    }

    private static ShellCommandDispatcher CreateDispatcher() =>
        CreateDispatcher(new FakeHandler());

    private static ShellCommandDispatcher CreateDispatcher(
        HttpMessageHandler handler)
    {
        FakeHttpClientFactory factory = new(handler);
        ArcanumApiClient client = new(factory, new FakeSecretStore());
        SessionWorkspaceService workspace = new(
            client,
            new NoopLastSessionStore(),
            NullLogger<SessionWorkspaceService>.Instance);
        return new ShellCommandDispatcher(
            client,
            new ShellCommandParser(),
            new TestOptionsMonitor(new ArcanumSettings()),
            workspace,
            new CommandCenterWardCoordinator(new CommandCenterHardModalArbiter()),
            NullLogger<ShellCommandDispatcher>.Instance);
    }

    private sealed class NoopLastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;

        public void SaveSessionId(Guid id)
        {
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:9") };
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"success":false,"error":{"code":"Test.Down","message":"down"}}"""),
                });
    }

    private sealed class RecordingSpellCatalogHandler : HttpMessageHandler
    {

        public Collection<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(request);

            SpellCatalogPage page = new(
                [new SpellSummary(
                    "catalog-spell",
                    "description",
                    SpellSource.Workspace,
                    [])],
                true,
                "opaque-next",
                "continue");

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<SpellCatalogPage>(page, true, null),
                ArcanumJsonContext.Default.ApiResponseSpellCatalogPage);

            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {

                    Content = new ByteArrayContent(payload),

                });

        }

    }

    private sealed class TestOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }
}

public sealed class CommandCenterSessionMutationGuardTests
{
    [Fact]
    public void TryDeny_when_idle_returns_false_without_mutating_footer()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Assert.False(CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? update));
        Assert.Null(update);
        Assert.Null(state.FooterHint);
    }

    [Fact]
    public void TryDeny_when_generating_sets_footer_and_returns_ui_update()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Assert.True(state.TryBeginTurn());
        Guid prior = Guid.NewGuid();
        state.SessionId = prior;

        Assert.True(CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? update));
        Assert.NotNull(update);
        Assert.Equal(CommandCenterUiUpdateKind.RefreshFooter, update!.Kind);
        Assert.Equal(CommandCenterSessionMutationGuard.GeneratingDenyMessage, state.FooterHint);
        Assert.Equal(prior, state.SessionId);
    }
}

public sealed class CommandCenterChatRunnerTests
{
    [Fact]
    public void ChatRunner_does_not_depend_on_ChatCommand()
    {
        System.Reflection.ConstructorInfo ctor = typeof(CommandCenterChatRunner).GetConstructors().Single();
        Assert.DoesNotContain(
            ctor.GetParameters(),
            p => p.ParameterType.Name.Contains("ChatCommand", StringComparison.Ordinal));
    }
}
