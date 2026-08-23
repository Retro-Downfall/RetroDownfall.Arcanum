using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterTurnAttachmentBuilderTests : IDisposable
{
    private readonly string _root;

    public CommandCenterTurnAttachmentBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "arcanum-cc-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Stages_text_file_from_at_token_and_strips_token()
    {
        string path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "hello attach", Encoding.UTF8);

        TurnAttachmentBuildResult result = CommandCenterTurnAttachmentBuilder.Build(
            $"please read @{path}",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings());

        Assert.DoesNotContain("@", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("please read", result.Prompt, StringComparison.Ordinal);
        Assert.NotNull(result.AttachedFiles);
        Assert.Single(result.AttachedFiles!);
        Assert.Equal("hello attach", result.AttachedFiles![0].Content);
        Assert.Null(result.ScryingFoci);
        Assert.Contains(result.StatusLines, static s => s.Contains("Staged:", StringComparison.Ordinal));
    }

    [Fact]
    public void Stages_png_as_scrying_focus()
    {
        string path = Path.Combine(_root, "shot.png");
        // Minimal PNG magic bytes + padding.
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        File.WriteAllBytes(path, png);

        TurnAttachmentBuildResult result = CommandCenterTurnAttachmentBuilder.Build(
            $"look at @{path}",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings());

        Assert.Null(result.AttachedFiles);
        Assert.NotNull(result.ScryingFoci);
        Assert.Single(result.ScryingFoci!);
        Assert.Equal("image/png", result.ScryingFoci![0].MimeType);
        Assert.Contains(result.StatusLines, static s => s.Contains("Scrying focus:", StringComparison.Ordinal));
    }

    [Fact]
    public void Pre_staged_path_from_attach_slash_is_included()
    {
        string path = Path.Combine(_root, "pre.txt");
        File.WriteAllText(path, "pre-staged", Encoding.UTF8);

        TurnAttachmentBuildResult result = CommandCenterTurnAttachmentBuilder.Build(
            "use the attach",
            workingDirectory: _root,
            preStagedPaths: [path],
            settings: DefaultSettings());

        Assert.NotNull(result.AttachedFiles);
        Assert.Single(result.AttachedFiles!);
        Assert.Equal("pre-staged", result.AttachedFiles![0].Content);
        Assert.True(result.ClearPreStagedAfterTurn);
    }

    [Fact]
    public void Missing_at_path_keeps_literal_token_and_reports_status()
    {
        TurnAttachmentBuildResult result = CommandCenterTurnAttachmentBuilder.Build(
            "see @missing-file.txt please",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings());

        Assert.Contains("@missing-file.txt", result.Prompt, StringComparison.Ordinal);
        Assert.Null(result.AttachedFiles);
        Assert.Contains(result.StatusLines, static s => s.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A staging failure must never rewrite the operator's message: the token is the only record of
    /// what they actually asked about, and the turn is dispatched (and billed) regardless.
    /// </summary>
    [Fact]
    public void Oversized_text_file_is_rejected_and_keeps_the_literal_token()
    {
        string path = Path.Combine(_root, "big.txt");
        long maxFileBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);
        File.WriteAllBytes(path, new byte[checked((int)maxFileBytes + 1)]);

        TurnAttachmentBuildResult result = CommandCenterTurnAttachmentBuilder.Build(
            $"summarize the failures in @{path} please",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings());

        Assert.Null(result.AttachedFiles);
        Assert.Contains(result.StatusLines, static s => s.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"@{path}", result.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            result.StatusLines,
            static s => s.Contains("literal token kept in the prompt", StringComparison.Ordinal));
    }

    /// <summary>
    /// An oversized inline image already keeps its token; the text branch must match it so one
    /// staging failure does not behave differently from the other.
    /// </summary>
    [Fact]
    public void Oversized_image_keeps_the_literal_token()
    {
        string path = Path.Combine(_root, "huge.png");
        byte[] png = new byte[2 * 1024 * 1024];
        png[0] = 0x89;
        png[1] = 0x50;
        png[2] = 0x4E;
        png[3] = 0x47;
        File.WriteAllBytes(path, png);

        TurnAttachmentBuildResult result = CommandCenterTurnAttachmentBuilder.Build(
            $"look at @{path} closely",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings());

        Assert.Null(result.ScryingFoci);
        Assert.Contains($"@{path}", result.Prompt, StringComparison.Ordinal);
    }

    private static ArcanumSettings DefaultSettings() =>
        new()
        {
            Features = new FeatureSettings { Scrying = true },
        };
}

/// <summary>
/// Submit reaches <see cref="CommandCenterChatRunner.RunTurnAsync"/> straight from the Terminal.Gui
/// key handler with nothing awaited in between, so anything the turn does before its first yield runs
/// on the main loop: no redraw, no spinner, and Ctrl+C never pumped.
/// </summary>
public sealed class CommandCenterTurnStartThreadingTests : IDisposable
{
    private readonly string _root;

    public CommandCenterTurnStartThreadingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "arcanum-cc-fifo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// A FIFO in the workspace is the unbounded case: <c>File.Exists</c> reports it as a file and the
    /// read never returns until a writer appears. Staged attachments are read and base64-encoded at
    /// submit time, so the turn has to reach its first yield before touching the filesystem — otherwise
    /// the TUI is wedged with no cancel path.
    /// </summary>
    [SkippableFact]
    public async Task A_turn_yields_before_it_reads_a_staged_attachment()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "mkfifo is POSIX-only; the blocking-read hazard this pins is a Unix file type.");

        string fifo = Path.Combine(_root, "trace.log");
        Assert.True(TryMakeFifo(fifo), "mkfifo did not create the FIFO.");

        CommandCenterChatRunner runner = CreateRunner();
        CommandCenterState state = new(new SessionLogBuffer()) { WorkingDirectory = _root };
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        Task turn = Task.CompletedTask;
        Thread mainLoop = new(() =>
            turn = runner.RunTurnAsync("summarize @trace.log", state, updates.Writer, CancellationToken.None))
        {
            IsBackground = true,
        };

        mainLoop.Start();
        bool yielded = mainLoop.Join(TimeSpan.FromSeconds(5));

        // Pair with the blocked reader — whichever thread it is on — so nothing is left wedged.
        await File.WriteAllTextAsync(fifo, "trace body");
        _ = mainLoop.Join(TimeSpan.FromSeconds(30));
        await turn.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            yielded,
            "RunTurnAsync blocked its caller reading a staged attachment; on the real submit path that "
                + "caller is the Terminal.Gui main loop.");
    }

    private static bool TryMakeFifo(string path)
    {
        using System.Diagnostics.Process? mkfifo = System.Diagnostics.Process.Start(
            new ProcessStartInfo("/usr/bin/mkfifo", [path]) { UseShellExecute = false });

        if (mkfifo is null)
        {
            return false;
        }

        mkfifo.WaitForExit();
        return mkfifo.ExitCode == 0 && File.Exists(path);
    }

    private static CommandCenterChatRunner CreateRunner()
    {
        string ndjson = JsonSerializer.Serialize(
            new IntelligenceEvent(IntelligenceEventType.Result, "done", "done"),
            ArcanumJsonContext.Default.IntelligenceEvent) + "\n";
        ArcanumApiClient client = new(new Factory(new StaticNdjsonHandler(ndjson)), new FakeSecretStore());
        SessionWorkspaceService workspace = new(
            client,
            new NoopLastSessionStore(),
            NullLogger<SessionWorkspaceService>.Instance);
        CommandCenterHardModalArbiter arbiter = new();
        return new CommandCenterChatRunner(
            client,
            new TestOptionsMonitor(new ArcanumSettings()),
            workspace,
            new CommandCenterWardCoordinator(arbiter),
            new CommandCenterHumanPromptCoordinator(client, arbiter),
            NullLogger<CommandCenterChatRunner>.Instance);
    }

    private sealed class StaticNdjsonHandler(string ndjson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
            });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://127.0.0.1:9"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
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

    private sealed class NoopLastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;

        public Task<ArcanumClientMutationResult<CliContextDocument>>
            SaveSessionIdAsync(
                Guid id,
                Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                ArcanumClientMutationResult<CliContextDocument>.Completed(
                    CliContextDocument.Empty with { SessionId = id }));
    }

    private sealed class TestOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }
}

public sealed class ShellCommandParserAttachTests
{
    private readonly ShellCommandParser _parser = new();

    [Fact]
    public void Attach_with_path_is_allowlisted()
    {
        ParsedShellCommand parsed = _parser.Parse("/attach ./notes.txt");
        Assert.Equal(ShellCommandKind.Attach, parsed.Kind);
        Assert.Equal("./notes.txt", parsed.Argument);
    }

    [Fact]
    public void Attach_without_path_asks_for_argument()
    {
        ParsedShellCommand parsed = _parser.Parse("/attach");
        Assert.Equal(ShellCommandKind.Attach, parsed.Kind);
        Assert.True(string.IsNullOrEmpty(parsed.Argument));
    }

    [Fact]
    public void Attachments_list_is_allowlisted()
    {
        ParsedShellCommand parsed = _parser.Parse("/attachments");
        Assert.Equal(ShellCommandKind.AttachmentsList, parsed.Kind);
        Assert.Null(parsed.Argument);
        Assert.Null(parsed.Version);
    }

    [Theory]
    [InlineData("/attachments add notes", "notes", null)]
    [InlineData("/attachments add notes v2", "notes", 2)]
    [InlineData("/attachments add notes 2", "notes", 2)]
    [InlineData("/attachments add shot.png v1", "shot.png", 1)]
    public void Attachments_add_parses_logical_name_and_optional_version(
        string input,
        string logicalName,
        int? version)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.AttachmentsAdd, parsed.Kind);
        Assert.Equal(logicalName, parsed.Argument);
        Assert.Equal(version, parsed.Version);
    }

    [Theory]
    [InlineData("/attachments reveal notes", "notes", null)]
    [InlineData("/attachments reveal notes v3", "notes", 3)]
    [InlineData("/attachments reveal notes 3", "notes", 3)]
    public void Attachments_reveal_parses_logical_name_and_optional_version(
        string input,
        string logicalName,
        int? version)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.AttachmentsReveal, parsed.Kind);
        Assert.Equal(logicalName, parsed.Argument);
        Assert.Equal(version, parsed.Version);
    }

    [Theory]
    [InlineData("/attachments foo")]
    [InlineData("/attachments add")]
    [InlineData("/attachments reveal")]
    [InlineData("/attachments add notes extra junk")]
    [InlineData("/attachments reveal notes vX")]
    public void Unknown_attachments_forms_are_denied_with_usage(string input)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);
        Assert.Contains("/attachments", parsed.DenialMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/attachment")]
    [InlineData("/attached")]
    [InlineData("/attaching")]
    public void Ambiguous_attach_star_forms_deny_and_mention_attachments(string input)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);
        Assert.Contains("/attachments", parsed.DenialMessage, StringComparison.Ordinal);
        Assert.Contains("/attach", parsed.DenialMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_is_not_confused_with_attach()
    {
        ParsedShellCommand list = _parser.Parse("/attachments");
        ParsedShellCommand attach = _parser.Parse("/attach notes.txt");
        Assert.Equal(ShellCommandKind.AttachmentsList, list.Kind);
        Assert.Equal(ShellCommandKind.Attach, attach.Kind);
    }

    [Theory]

    [InlineData("/attachments refresh notes", "notes")]

    [InlineData("/attachments refresh notes.txt", "notes.txt")]

    public void Attachments_refresh_parses_logical_name(string input, string logicalName)

    {

        ParsedShellCommand parsed = _parser.Parse(input);

        Assert.Equal(ShellCommandKind.AttachmentsRefresh, parsed.Kind);

        Assert.Equal(logicalName, parsed.Argument);

    }
}

public sealed class SessionAttachmentRevealTests
{
    [Fact]
    public void Unattended_reveal_is_noop_with_status()
    {
        string status = SessionAttachmentReveal.TryReveal(
            "/tmp/example.txt",
            isInteractive: false,
            out bool started);

        Assert.False(started);
        Assert.Contains("interactive", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Builds_macos_reveal_start_info_with_argument_list()
    {
        Assert.True(
            SessionAttachmentReveal.TryBuildStartInfo(
                "/tmp/notes.txt",
                OperatingSystemFamily.MacOS,
                out ProcessStartInfo? psi,
                out string? error));
        Assert.Null(error);
        Assert.NotNull(psi);
        Assert.Equal("open", psi!.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(["-R", "/tmp/notes.txt"], psi.ArgumentList.ToArray());
    }

    [Fact]
    public void Builds_windows_reveal_start_info()
    {
        Assert.True(
            SessionAttachmentReveal.TryBuildStartInfo(
                @"C:\tmp\notes.txt",
                OperatingSystemFamily.Windows,
                out ProcessStartInfo? psi,
                out string? error));
        Assert.Null(error);
        Assert.NotNull(psi);
        Assert.Equal("explorer", psi!.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal([@"/select,C:\tmp\notes.txt"], psi.ArgumentList.ToArray());
    }

    [Fact]
    public void Builds_linux_reveal_start_info_for_parent_dir()
    {
        const string attachmentPath = "/tmp/dir/notes.txt";

        Assert.True(
            SessionAttachmentReveal.TryBuildStartInfo(
                attachmentPath,
                OperatingSystemFamily.Linux,
                out ProcessStartInfo? psi,
                out string? error));
        Assert.Null(error);
        Assert.NotNull(psi);
        Assert.Equal("xdg-open", psi!.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal([Path.GetDirectoryName(attachmentPath)!], psi.ArgumentList.ToArray());
    }
}

public sealed class ShellCommandDispatcherAttachmentsTests
{
    [Fact]
    public async Task Attachments_list_requires_session()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => Down());
        CommandCenterState state = new(new SessionLogBuffer());

        _ = await dispatcher.DispatchAsync("/attachments", state, CancellationToken.None);

        Assert.Contains("session", state.Log.RenderPlainText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(state.StagedAttachmentReferences);
    }

    [Fact]
    public async Task Attachments_list_formats_api_rows_into_transcript()
    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        SessionAttachmentDto[] payload =
        [
            new(
                attachmentId,
                "notes",
                "notes.txt",
                1,
                $"{sessionId:N}/notes/v1/notes.txt",
                "text/plain",
                11,
                SessionAttachmentKind.Text,
                "abc123",
                DateTimeOffset.Parse("2026-07-19T12:00:00Z")),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(req =>
        {
            Assert.Equal($"/api/sessions/{sessionId:D}/attachments", req.RequestUri!.AbsolutePath);
            return OkAttachments(payload);
        });
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments", state, CancellationToken.None);

        string text = state.Log.RenderPlainText();
        Assert.Contains("notes", text, StringComparison.Ordinal);
        Assert.Contains("v1", text, StringComparison.Ordinal);
        Assert.Contains("notes.txt", text, StringComparison.Ordinal);
    }

    [Fact]

    public async Task Attachments_list_renders_authoritative_snapshot_live_and_stale_badges_with_versions()

    {

        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        DateTimeOffset observed = DateTimeOffset.Parse("2026-08-01T15:04:05Z");

        SessionAttachmentDto[] payload =

        [

            Attachment(sessionId, "snapshot", "SNAPSHOT-HASH"),

            Attachment(

                sessionId,

                "live",

                "LIVE-CONTENT-HASH",

                AttachmentSourceKind.WorkspaceFile,

                AttachmentSourceStatus.Refreshable,

                true,

                "src/live.txt",

                "LIVE-CONTENT-HASH",

                observed),

            Attachment(

                sessionId,

                "stale",

                "LOADED-CONTENT-HASH",

                AttachmentSourceKind.WorkspaceFile,

                AttachmentSourceStatus.PriorVersion,

                false,

                "src/stale.txt",

                "DISK-CONTENT-HASH",

                observed),

        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => OkAttachments(payload));

        CommandCenterState state = new(new SessionLogBuffer());

        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments", state, CancellationToken.None);

        string text = state.Log.RenderPlainText();

        Assert.Contains("[Snapshot]", text, StringComparison.Ordinal);

        Assert.Contains("[Live]", text, StringComparison.Ordinal);

        Assert.Contains("[Stale]", text, StringComparison.Ordinal);

        Assert.Contains("loaded=LIVE-CON", text, StringComparison.Ordinal);

        Assert.Contains("disk=DISK-CON", text, StringComparison.Ordinal);

        Assert.Contains("2026-08-01T15:04:05", text, StringComparison.Ordinal);

    }

    [Fact]

    public async Task Attachments_refresh_posts_selected_attachment_and_renders_backend_confirmation()

    {

        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        SessionAttachmentDto row = Attachment(

            sessionId,

            "notes",

            "OLD-HASH",

            AttachmentSourceKind.WorkspaceFile,

            AttachmentSourceStatus.PriorVersion,

            false,

            "src/notes.txt",

            "NEW-HASH",

            DateTimeOffset.Parse("2026-08-01T15:00:00Z"),

            attachmentId);

        int requests = 0;

        ShellCommandDispatcher dispatcher = CreateDispatcher(request =>

        {

            requests++;

            if (request.Method == HttpMethod.Get)

            {

                return OkAttachments([row]);

            }

            Assert.Equal(HttpMethod.Post, request.Method);

            Assert.Equal(

                $"/api/sessions/{sessionId:D}/attachments/{attachmentId:D}/refresh",

                request.RequestUri!.AbsolutePath);

            return OkAttachmentRefresh(new AttachmentRefreshEvent(

                Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee"),

                "notes",

                2,

                true,

                false,

                "src/notes.txt",

                "CURRENT-HASH",

                12,

                DateTimeOffset.Parse("2026-08-01T15:05:00Z")));

        });

        CommandCenterState state = new(new SessionLogBuffer());

        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments refresh notes", state, CancellationToken.None);

        string text = state.Log.RenderPlainText();

        Assert.Equal(3, requests);

        Assert.Contains("Live", text, StringComparison.Ordinal);

        Assert.Contains("v2", text, StringComparison.Ordinal);

        Assert.Contains("CURRENT-", text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Attachments_add_stages_guid_reference()
    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        SessionAttachmentDto[] payload =
        [
            new(
                attachmentId,
                "notes",
                "notes.txt",
                2,
                $"{sessionId:N}/notes/v2/notes.txt",
                "text/plain",
                11,
                SessionAttachmentKind.Text,
                "abc123",
                DateTimeOffset.Parse("2026-07-19T12:00:00Z")),
            new(
                Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "notes",
                "notes.txt",
                1,
                $"{sessionId:N}/notes/v1/notes.txt",
                "text/plain",
                10,
                SessionAttachmentKind.Text,
                "def456",
                DateTimeOffset.Parse("2026-07-19T11:00:00Z")),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => OkAttachments(payload));
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments add notes", state, CancellationToken.None);

        Assert.Contains(attachmentId, state.StagedAttachmentReferences);
        Assert.DoesNotContain(
            Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee"),
            state.StagedAttachmentReferences);
        Assert.Contains("Staged", state.Log.RenderPlainText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Attachments_add_with_version_stages_that_version()
    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid v1 = Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee");
        Guid v2 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        SessionAttachmentDto[] payload =
        [
            new(v2, "notes", "notes.txt", 2, "p/v2", "text/plain", 11, SessionAttachmentKind.Text, "a", DateTimeOffset.UtcNow),
            new(v1, "notes", "notes.txt", 1, "p/v1", "text/plain", 10, SessionAttachmentKind.Text, "b", DateTimeOffset.UtcNow),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => OkAttachments(payload));
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments add notes v1", state, CancellationToken.None);

        Assert.Contains(v1, state.StagedAttachmentReferences);
        Assert.DoesNotContain(v2, state.StagedAttachmentReferences);
    }

    [Fact]
    public async Task Attachments_reveal_reports_absolute_path_under_attachments_directory()
    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        string relative = $"{sessionId:N}/notes/v1/notes.txt";
        SessionAttachmentDto[] payload =
        [
            new(
                attachmentId,
                "notes",
                "notes.txt",
                1,
                relative,
                "text/plain",
                11,
                SessionAttachmentKind.Text,
                "abc123",
                DateTimeOffset.UtcNow),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => OkAttachments(payload));
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments reveal notes", state, CancellationToken.None);

        string expected = Path.GetFullPath(Path.Combine(ArcanumPaths.AttachmentsDirectory, relative));
        string text = state.Log.RenderPlainText();
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Staged_attachment_references_can_be_cleared_like_paths()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Guid id = Guid.NewGuid();
        _ = state.StagedAttachmentReferences.Add(id);
        Assert.Single(state.StagedAttachmentReferences);
        state.StagedAttachmentReferences.Clear();
        Assert.Empty(state.StagedAttachmentReferences);
    }

    private static ShellCommandDispatcher CreateDispatcher(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        RecordingHandler handler = new(respond);
        ArcanumApiClient client = new(
            new Factory(handler),
            new FakeSecretStore());
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

    private static HttpResponseMessage OkAttachments(SessionAttachmentDto[] payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<SessionAttachmentDto[]>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static SessionAttachmentDto Attachment(

        Guid sessionId,

        string logicalKey,

        string contentSha256,

        AttachmentSourceKind sourceKind = AttachmentSourceKind.SnapshotOnly,

        AttachmentSourceStatus sourceStatus = AttachmentSourceStatus.NotApplicable,

        bool isRefreshable = false,

        string? sourceRelativePath = null,

        string? observedSha256 = null,

        DateTimeOffset? observedWriteTime = null,

        Guid? id = null) =>

        new(

            id ?? Guid.NewGuid(),

            logicalKey,

            logicalKey + ".txt",

            1,

            $"{sessionId:N}/{logicalKey}/v1/{logicalKey}.txt",

            "text/plain",

            11,

            SessionAttachmentKind.Text,

            contentSha256,

            DateTimeOffset.Parse("2026-08-01T14:00:00Z"),

            sourceKind,

            SourceWorkspaceIdentity: sourceKind == AttachmentSourceKind.WorkspaceFile ? "workspace" : null,

            sourceRelativePath,

            isRefreshable,

            sourceStatus,

            LastObservedSourceContentSha256: observedSha256,

            LastObservedSourceWriteTime: observedWriteTime);

    private static HttpResponseMessage OkAttachmentRefresh(AttachmentRefreshEvent payload)

    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(

            new ApiResponse<AttachmentRefreshEvent>(payload, true, null),

            ArcanumJsonContext.Default.ApiResponseAttachmentRefreshEvent);

        HttpResponseMessage response = new(HttpStatusCode.OK)

        {

            Content = new ByteArrayContent(json),

        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return response;

    }

    private static HttpResponseMessage Down() =>
        new(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"success":false,"error":{"code":"Test.Down","message":"down"}}"""),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
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

    private sealed class NoopLastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;

        public Task<ArcanumClientMutationResult<CliContextDocument>>
            SaveSessionIdAsync(
                Guid id,
                Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                ArcanumClientMutationResult<CliContextDocument>.Completed(
                    CliContextDocument.Empty with { SessionId = id }));
    }

    private sealed class TestOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }
}

public sealed class CommandCenterAttachmentDriftMonitorTests

{

    [Fact]

    public void Backend_snapshot_is_the_only_authority_for_live_to_stale_transition()

    {

        Guid id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        CommandCenterState state = new(new SessionLogBuffer())

        {

            SessionAttachments =

            [

                Attachment(id, AttachmentSourceStatus.Refreshable, true, "LOADED"),

            ],

        };

        IReadOnlyList<string> notices = CommandCenterAttachmentDriftMonitor.ApplyBackendSnapshot(

            state,

            [Attachment(id, AttachmentSourceStatus.PriorVersion, false, "DISK")]);

        string notice = Assert.Single(notices);

        Assert.Contains("[Stale]", notice, StringComparison.Ordinal);

        Assert.Contains("DISK", notice, StringComparison.Ordinal);

        Assert.Equal(AttachmentSourceStatus.PriorVersion, state.SessionAttachments[0].SourceStatus);

    }

    private static SessionAttachmentDto Attachment(

        Guid id,

        AttachmentSourceStatus status,

        bool refreshable,

        string observedHash) =>

        new(

            id,

            "notes",

            "notes.txt",

            1,

            "session/notes/v1/notes.txt",

            "text/plain",

            10,

            SessionAttachmentKind.Text,

            "LOADED",

            DateTimeOffset.Parse("2026-08-01T14:00:00Z"),

            AttachmentSourceKind.WorkspaceFile,

            "workspace",

            "src/notes.txt",

            refreshable,

            status,

            LastObservedSourceContentSha256: observedHash,

            LastObservedSourceWriteTime: DateTimeOffset.Parse("2026-08-01T15:00:00Z"));

}
