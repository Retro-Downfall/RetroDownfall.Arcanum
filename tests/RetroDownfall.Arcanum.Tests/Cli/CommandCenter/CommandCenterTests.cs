using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class ShellCommandParserTests
{
    private readonly ShellCommandParser _parser = new();

    [Theory]
    [InlineData("/help", "Help")]
    [InlineData("/exit", "Exit")]
    [InlineData("/quit", "Quit")]
    [InlineData("/clear", "Clear")]
    [InlineData("/status", "Status")]
    [InlineData("/doctor", "Doctor")]
    [InlineData("/mcp", "Mcp")]
    [InlineData("/arsenal", "Arsenal")]
    [InlineData("/tools", "Tools")]
    [InlineData("/mana", "Mana")]
    [InlineData("/keys", "Keys")]
    [InlineData("/model list", "ModelList")]
    [InlineData("/provider list", "ProviderList")]
    [InlineData("/campaign list", "CampaignList")]
    [InlineData("/session list", "SessionList")]
    [InlineData("/session new", "SessionNew")]
    [InlineData("/spell list", "SpellList")]
    [InlineData("/ward list", "WardList")]
    [InlineData("/ward allow", "WardAllow")]
    [InlineData("/ward deny", "WardDeny")]
    public void Parses_allowlisted_commands(string input, string expectedKind)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(Enum.Parse<ShellCommandKind>(expectedKind), parsed.Kind);
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
        ParsedShellCommand parsed = _parser.Parse($"/session resume {id:D}");
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
    public async Task Session_resume_invalid_guid_errors_without_binding()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher();
        CommandCenterState state = new(new SessionLogBuffer());

        ShellDispatchResult result = await dispatcher.DispatchAsync(
            "/session resume not-a-guid",
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
            "/session resume 33333333-3333-3333-3333-333333333333",
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

        _ = await dispatcher.DispatchAsync("/session new", state, CancellationToken.None);

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

        _ = await dispatcher.DispatchAsync("/session new", state, CancellationToken.None);

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

        _ = await dispatcher.DispatchAsync($"/session resume {target:D}", state, CancellationToken.None);

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

    private static ShellCommandDispatcher CreateDispatcher()
    {
        FakeHttpClientFactory factory = new(new FakeHandler());
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
            new CommandCenterWardCoordinator(),
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
