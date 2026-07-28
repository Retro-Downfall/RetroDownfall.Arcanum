using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;

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

    [Fact]
    public void Oversized_text_file_is_rejected()
    {
        string path = Path.Combine(_root, "big.txt");
        File.WriteAllText(path, new string('x', 2048), Encoding.UTF8);

        ArcanumSettings settings = DefaultSettings();
        settings.Cli.MaxAttachFileSizeBytes = 1024;

        TurnAttachmentBuildResult result = CommandCenterTurnAttachmentBuilder.Build(
            $"@{path}",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: settings);

        Assert.Null(result.AttachedFiles);
        Assert.Contains(result.StatusLines, static s => s.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
    }

    private static ArcanumSettings DefaultSettings() =>
        new()
        {
            Cli = new CliSettings { MaxAttachFileSizeBytes = 1_048_576L },
            Scrying = new ScryingSettings
            {
                Enabled = true,
                MaxImageBytes = 1_048_576L,
                AllowedMimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"],
            },
        };
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

        public void SaveSessionId(Guid id)
        {
        }
    }

    private sealed class TestOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }
}
