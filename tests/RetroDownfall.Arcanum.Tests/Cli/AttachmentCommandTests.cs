using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Chronosync;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Pattern;

using RetroDownfall.Arcanum.Core.Pattern.Entities;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class AttachmentCommandTests
{

    private static readonly Guid SessionId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid FirstAttachmentId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static readonly Guid SecondAttachmentId =
        Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    private static readonly Guid PinId =
        Guid.Parse("99999999-8888-7777-6666-555555555555");

    [Fact]

    public async Task Help_exposes_complete_attachment_command_family()
    {

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            ["attachment", "--help"]);

        Assert.Equal(0, result.ExitCode);

        foreach (string command in new[]
                 {
                     "list",
                     "add",
                     "reference",
                     "show",
                     "versions",
                     "refresh",
                     "pin",
                     "unpin",
                     "export",
                     "reveal",
                 })
        {

            Assert.Contains(command, result.Output, StringComparison.OrdinalIgnoreCase);

        }

        Assert.Empty(handler.Requests);

    }

    [Fact]

    public async Task Privacy_disclosure_needs_no_session_or_http_and_promises_no_terminal_bytes()
    {

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            ["attachment", "show", "--privacy"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("privacy", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("never", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("attachment bytes", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("terminal", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task List_shows_latest_versions_while_versions_shows_full_history()
    {

        RecordingHandler listHandler = new(
            attachmentListEnvelope: VersionedAttachmentListEnvelope);

        CliTestResult list = await RunCommandAsync(
            listHandler,
            [
                "--json",
                "attachment",
                "list",
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, list.ExitCode);

        Assert.Contains("\"version\":2", list.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("\"version\":1", list.Output, StringComparison.Ordinal);

        RecordingHandler versionsHandler = new(
            attachmentListEnvelope: VersionedAttachmentListEnvelope);

        CliTestResult versions = await RunCommandAsync(
            versionsHandler,
            [
                "--json",
                "attachment",
                "versions",
                "notes",
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, versions.ExitCode);

        Assert.Contains("\"version\":1", versions.Output, StringComparison.Ordinal);

        Assert.Contains("\"version\":2", versions.Output, StringComparison.Ordinal);

    }

    [Fact]

    public async Task List_preserves_case_distinct_logical_keys()
    {

        RecordingHandler handler = new(
            attachmentListEnvelope: CaseDistinctAttachmentListEnvelope);

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "--json",
                "attachment",
                "list",
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement rows = document.RootElement;

        Assert.Equal(2, rows.GetArrayLength());

        JsonElement upper = Assert.Single(
            rows.EnumerateArray(),
            row => row.GetProperty("logicalKey").GetString() == "Notes");

        JsonElement lower = Assert.Single(
            rows.EnumerateArray(),
            row => row.GetProperty("logicalKey").GetString() == "notes");

        Assert.Equal(2, upper.GetProperty("version").GetInt32());

        Assert.Equal(3, lower.GetProperty("version").GetInt32());

    }

    [Fact]

    public async Task Case_distinct_logical_key_selector_is_ambiguous_but_exact_guid_remains_usable()
    {

        RecordingHandler ambiguousHandler = new(
            attachmentListEnvelope: CaseDistinctAttachmentListEnvelope);

        CliTestResult ambiguous = await RunCommandAsync(
            ambiguousHandler,
            [
                "attachment",
                "show",
                "notes",
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(1, ambiguous.ExitCode);

        Assert.Contains("ambiguous", ambiguous.Error, StringComparison.OrdinalIgnoreCase);

        RecordingHandler exactHandler = new(
            attachmentListEnvelope: CaseDistinctAttachmentListEnvelope);

        CliTestResult exact = await RunCommandAsync(
            exactHandler,
            [
                "--json",
                "attachment",
                "show",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, exact.ExitCode);

        using JsonDocument document = JsonDocument.Parse(exact.Output);

        Assert.Equal(
            FirstAttachmentId,
            document.RootElement.GetProperty("id").GetGuid());

        Assert.Equal(
            "Notes",
            document.RootElement.GetProperty("logicalKey").GetString());

        Assert.Equal(
            1,
            document.RootElement.GetProperty("version").GetInt32());

    }

    [Fact]

    public async Task Versions_does_not_merge_case_distinct_histories()
    {

        RecordingHandler ambiguousHandler = new(
            attachmentListEnvelope: CaseDistinctAttachmentListEnvelope);

        CliTestResult ambiguous = await RunCommandAsync(
            ambiguousHandler,
            [
                "attachment",
                "versions",
                "notes",
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(1, ambiguous.ExitCode);

        Assert.Contains("ambiguous", ambiguous.Error, StringComparison.OrdinalIgnoreCase);

        RecordingHandler exactHandler = new(
            attachmentListEnvelope: CaseDistinctAttachmentListEnvelope);

        CliTestResult exact = await RunCommandAsync(
            exactHandler,
            [
                "--json",
                "attachment",
                "versions",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, exact.ExitCode);

        using JsonDocument document = JsonDocument.Parse(exact.Output);

        JsonElement rows = document.RootElement;

        Assert.Equal(2, rows.GetArrayLength());

        Assert.All(
            rows.EnumerateArray(),
            row => Assert.Equal(
                "Notes",
                row.GetProperty("logicalKey").GetString()));

        Assert.Equal(
            [2, 1],
            rows.EnumerateArray()
                .Select(row => row.GetProperty("version").GetInt32())
                .ToArray());

    }

    [Fact]

    public async Task List_accepts_positional_session_with_session_option_precedence()
    {

        RecordingHandler positionalHandler = new();

        CliTestResult positional = await RunCommandAsync(
            positionalHandler,
            [
                "attachment",
                "list",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, positional.ExitCode);

        Assert.Contains(
            positionalHandler.Requests,
            request => request.Path == $"/api/sessions/{SessionId:D}/attachments");

        Guid ignoredPositional = Guid.Parse(
            "feedface-0000-1111-2222-333333333333");

        RecordingHandler precedenceHandler = new();

        CliTestResult precedence = await RunCommandAsync(
            precedenceHandler,
            [
                "attachment",
                "list",
                ignoredPositional.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, precedence.ExitCode);

        Assert.Contains(
            precedenceHandler.Requests,
            request => request.Path == $"/api/sessions/{SessionId:D}/attachments");

        Assert.DoesNotContain(
            precedenceHandler.Requests,
            request => request.Path.Contains(
                ignoredPositional.ToString("D"),
                StringComparison.OrdinalIgnoreCase));

    }

    [Fact]

    public async Task Human_list_includes_filename_and_redacts_paths_in_source_reason()
    {

        RecordingHandler handler = new(
            attachmentListEnvelope: DiagnosticAttachmentListEnvelope);

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "list",
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("notes.txt", result.Output, StringComparison.Ordinal);

        Assert.Contains("Reason=", result.Output, StringComparison.Ordinal);

        Assert.Contains("[path]", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("/srv/private", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public async Task Refresh_posts_to_server_even_when_selected_row_is_snapshot_only()
    {

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "refresh",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, result.ExitCode);

        RecordedRequest refresh = Assert.Single(
            handler.Requests,
            request => request.Method == HttpMethod.Post
                && request.Path.EndsWith("/refresh", StringComparison.Ordinal));

        Assert.Equal(
            $"/api/sessions/{SessionId:D}/attachments/{FirstAttachmentId:D}/refresh",
            refresh.Path);

    }

    [Fact]

    public async Task Pin_and_unpin_use_context_pins_with_exact_attachment_id()
    {

        RecordingHandler pinHandler = new();

        CliTestResult pin = await RunCommandAsync(
            pinHandler,
            [
                "attachment",
                "pin",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, pin.ExitCode);

        RecordedRequest create = Assert.Single(
            pinHandler.Requests,
            request => request.Method == HttpMethod.Post
                && request.Path.EndsWith("/context-pins", StringComparison.Ordinal));

        using (JsonDocument document = JsonDocument.Parse(create.Body))
        {

            Assert.Equal(
                FirstAttachmentId.ToString("D"),
                document.RootElement
                    .GetProperty("targetIdentifier")
                    .GetString());

        }

        RecordingHandler unpinHandler = new();

        CliTestResult unpin = await RunCommandAsync(
            unpinHandler,
            [
                "attachment",
                "unpin",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(0, unpin.ExitCode);

        RecordedRequest delete = Assert.Single(
            unpinHandler.Requests,
            request => request.Method == HttpMethod.Delete);

        Assert.Equal(
            $"/api/sessions/{SessionId:D}/context-pins/{PinId:D}",
            delete.Path);

    }

    [Fact]

    public async Task Export_atomically_replaces_destination_without_writing_content_to_stdout()
    {

        byte[] content = Encoding.UTF8.GetBytes("export-canary-content-26");

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-attachment-export-{Guid.NewGuid():N}");

        string destination = Path.Combine(directory, "notes.txt");

        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(destination, "original-content");

        try
        {

            RecordingHandler handler = new(attachmentContent: content);

            CliTestResult result = await RunCommandAsync(
                handler,
                [
                    "--yes",
                    "attachment",
                    "export",
                    FirstAttachmentId.ToString("D"),
                    "--output",
                    destination,
                    "--session",
                    SessionId.ToString("D"),
                ]);

            Assert.Equal(0, result.ExitCode);

            Assert.Equal(content, await File.ReadAllBytesAsync(destination));

            Assert.DoesNotContain(
                Encoding.UTF8.GetString(content),
                result.Output,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                Encoding.UTF8.GetString(content),
                result.Error,
                StringComparison.Ordinal);

            Assert.Empty(
                Directory.EnumerateFiles(
                    directory,
                    ".*.download",
                    SearchOption.TopDirectoryOnly));

        }
        finally
        {

            Directory.Delete(directory, recursive: true);

        }

    }

    [Fact]

    public async Task Reveal_rejects_an_api_relative_path_that_escapes_attachment_storage()
    {

        RecordingHandler handler = new(
            attachmentListEnvelope: EscapingAttachmentListEnvelope);

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "reveal",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("unsafe", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Reveal_missing_local_server_artifact_does_not_launch_and_recommends_export()
    {

        using TestHomeScope home = new();

        FakeAttachmentRevealLauncher launcher = new();

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "reveal",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ],
            revealLauncher: launcher);

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(0, launcher.Attempts);

        Assert.Contains("not locally available", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("attachment export", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Reveal_non_envelope_local_target_does_not_launch_and_recommends_export()
    {

        using TestHomeScope home = new();

        string target = home.ResolveAttachmentPath(
            "session/stdin/v1/stdin.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        await File.WriteAllBytesAsync(
            target,
            "unrelated-local-file"u8.ToArray());

        FakeAttachmentRevealLauncher launcher = new();

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "reveal",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ],
            revealLauncher: launcher);

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(0, launcher.Attempts);

        Assert.Contains("not locally available", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("attachment export", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Reveal_local_arcablob_artifact_launches_file_manager()
    {

        using TestHomeScope home = new();

        string target = home.ResolveAttachmentPath(
            "session/stdin/v1/stdin.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        await File.WriteAllBytesAsync(
            target,
            "ARCABLOBpayload"u8.ToArray());

        FakeAttachmentRevealLauncher launcher = new();

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "reveal",
                FirstAttachmentId.ToString("D"),
                "--session",
                SessionId.ToString("D"),
            ],
            revealLauncher: launcher);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(1, launcher.Attempts);

        Assert.Equal(target, launcher.LastPath);

    }

    [Fact]

    public async Task Add_from_stdin_streams_multipart_without_echoing_attachment_bytes()
    {

        const string SecretAttachmentBytes = "stdin-canary-attachment-body-26";

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "add",
                "-",
                "--mime",
                "text/plain",
                "--name",
                "stdin.txt",
                "--session",
                SessionId.ToString("D"),
            ],
            SecretAttachmentBytes);

        Assert.Equal(0, result.ExitCode);

        RecordedRequest request = Assert.Single(
            handler.Requests,
            static request =>
                request.Method == HttpMethod.Post
                && request.Path.Contains("/attachments", StringComparison.Ordinal));

        Assert.Contains(SessionId.ToString("D"), request.Path, StringComparison.OrdinalIgnoreCase);

        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(SecretAttachmentBytes, request.Body, StringComparison.Ordinal);

        Assert.Contains("stdin.txt", request.Body, StringComparison.Ordinal);

        Assert.Contains("text/plain", request.Body, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(SecretAttachmentBytes, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(SecretAttachmentBytes, result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public async Task Add_from_stdin_defaults_to_text_filename_and_mime()
    {

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "add",
                "-",
                "--session",
                SessionId.ToString("D"),
            ],
            "ordinary piped text");

        Assert.Equal(0, result.ExitCode);

        RecordedRequest request = Assert.Single(
            handler.Requests,
            static request => request.Method == HttpMethod.Post
                && request.Path.Contains("/attachments", StringComparison.Ordinal));

        Assert.Contains("stdin.txt", request.Body, StringComparison.Ordinal);

        Assert.Contains("text/plain", request.Body, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("ordinary piped text", request.Body, StringComparison.Ordinal);

    }

    [Theory]

    [InlineData("text/plain", "stdin.txt")]

    [InlineData("application/json", "stdin.json")]

    [InlineData("image/png", "stdin.png")]

    [InlineData("image/jpeg", "stdin.jpg")]

    [InlineData("image/gif", "stdin.gif")]

    [InlineData("image/webp", "stdin.webp")]

    [InlineData("application/octet-stream", "stdin.bin")]

    public async Task Add_from_stdin_mime_derives_a_compatible_filename(
        string mimeType,
        string expectedFilename)
    {

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "attachment",
                "add",
                "-",
                "--mime",
                mimeType,
                "--session",
                SessionId.ToString("D"),
            ],
            "mime-specific stdin");

        Assert.Equal(0, result.ExitCode);

        RecordedRequest request = Assert.Single(
            handler.Requests,
            static request => request.Method == HttpMethod.Post
                && request.Path.Contains("/attachments", StringComparison.Ordinal));

        Assert.Contains(expectedFilename, request.Body, StringComparison.Ordinal);

        Assert.Contains(mimeType, request.Body, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Reference_forwards_server_workspace_path_without_client_resolution()
    {

        const string ServerPath = "../server-path";

        const string WorkspaceId = "ws-server";

        string root = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-attachment-reference-{Guid.NewGuid():N}");

        string clientDirectory = Path.Combine(root, "client-only");

        string originalDirectory = global::System.Environment.CurrentDirectory;

        Directory.CreateDirectory(clientDirectory);

        try
        {

            global::System.Environment.CurrentDirectory = clientDirectory;

            Assert.False(File.Exists(Path.GetFullPath(ServerPath)));

            Assert.False(Directory.Exists(Path.GetFullPath(ServerPath)));

            RecordingHandler handler = new();

            CliTestResult result = await RunCommandAsync(
                handler,
                [
                    "attachment",
                    "reference",
                    ServerPath,
                    "--workspace",
                    WorkspaceId,
                    "--session",
                    SessionId.ToString("D"),
                ]);

            Assert.Equal(0, result.ExitCode);

            RecordedRequest request = Assert.Single(
                handler.Requests,
                request =>
                    request.Method == HttpMethod.Post
                    && request.Body.Contains(ServerPath, StringComparison.Ordinal));

            Assert.Contains(ServerPath, request.Body, StringComparison.Ordinal);

            Assert.Contains(WorkspaceId, request.Body, StringComparison.Ordinal);

            Assert.DoesNotContain(
                Path.GetFullPath(ServerPath),
                request.Body,
                StringComparison.Ordinal);

        }
        finally
        {

            global::System.Environment.CurrentDirectory = originalDirectory;

            Directory.Delete(root, recursive: true);

        }

    }

    /// <summary>
    /// Binding an already-persisted attachment by GUID is capability that used to live only on
    /// <c>ask</c>; collapsing to one one-shot entry had to carry it over, not drop it.
    /// </summary>
    [Fact]
    public async Task Run_repeatable_attachment_options_serialize_reference_ids()
    {

        RecordingHandler handler = new();

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "run",
                "--session",
                SessionId.ToString("D"),
                "--attachment",
                FirstAttachmentId.ToString("D"),
                "--attachment",
                SecondAttachmentId.ToString("D"),
                "question",
            ]);

        Assert.Equal(0, result.ExitCode);

        RecordedRequest ping = Assert.Single(
            handler.Requests,
            static request => request.Path == "/api/intelligence/ping-stream");

        AssertAttachmentReferences(
            ping.Body,
            FirstAttachmentId,
            SecondAttachmentId);

    }

    /// <summary>
    /// A failed one-shot turn must not silently swallow the attachment references it was asked to
    /// bind: the failure surfaces and the request that was attempted still carried them. The
    /// multi-turn retry this replaced belonged to the removed frameless REPL.
    /// </summary>
    [Fact]
    public async Task Run_reports_a_failed_turn_without_losing_the_attachment_references()
    {

        RecordingHandler handler = new(failFirstPing: true);

        CliTestResult result = await RunCommandAsync(
            handler,
            [
                "run",
                "--session",
                SessionId.ToString("D"),
                "--attachment",
                FirstAttachmentId.ToString("D"),
                "--attachment",
                SecondAttachmentId.ToString("D"),
                "question",
            ]);

        RecordedRequest ping = Assert.Single(
            handler.Requests,
            static request => request.Path == "/api/intelligence/ping-stream");

        AssertAttachmentReferences(
            ping.Body,
            FirstAttachmentId,
            SecondAttachmentId);

        Assert.NotEqual(0, result.ExitCode);

    }

    private static void AssertAttachmentReferences(
        string requestBody,
        params Guid[] expected)
    {

        using JsonDocument document = JsonDocument.Parse(requestBody);

        JsonElement references = document.RootElement.GetProperty("attachmentReferences");

        Guid[] actual = references
            .EnumerateArray()
            .Select(static value => value.GetGuid())
            .ToArray();

        Assert.Equal(expected, actual);

    }

    private static async Task<CliTestResult> RunCommandAsync(
        RecordingHandler handler,
        string[] args,
        string? input = null,
        IAttachmentRevealLauncher? revealLauncher = null)
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(
            new FakeSecretStore());

        services.RemoveAll<ICliInferenceContextResolver>();

        services.AddSingleton<ICliInferenceContextResolver>(
            new FakeContextResolver());

        services.RemoveAll<IEyeOfTheWorld>();

        services.AddSingleton<IEyeOfTheWorld>(
            new FakeEye());

        services.RemoveAll<IGrimoireCliInitialization>();

        services.AddSingleton<IGrimoireCliInitialization>(
            new NoopGrimoireInitialization());

        services.RemoveAll<IChronosyncEngine>();

        services.AddSingleton<IChronosyncEngine>(
            new NoopChronosyncEngine());

        services.RemoveAll<IArcanumServeLauncher>();

        services.AddSingleton<IArcanumServeLauncher>(
            new NoopServeLauncher());

        if (revealLauncher is not null)
        {

            services.RemoveAll<IAttachmentRevealLauncher>();

            services.AddSingleton(revealLauncher);

        }

        return await CliTestHarness
            .RunAsync(services, args, input)
            .ConfigureAwait(false);

    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {

            Content = new StringContent(json, Encoding.UTF8, "application/json"),

        };

    private static HttpResponseMessage NdjsonResponse(string ndjson) =>
        new(HttpStatusCode.OK)
        {

            Content = new StringContent(
                ndjson,
                Encoding.UTF8,
                "application/x-ndjson"),

        };

    private static string SerializeFrames(params IntelligenceEvent[] frames) =>
        string.Join(
            '\n',
            frames.Select(static frame =>
                JsonSerializer.Serialize(
                    frame,
                    ArcanumJsonContext.Default.IntelligenceEvent)))
        + "\n";

    private sealed class FakeHttpClientFactory(
        RecordingHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001/"),

                Timeout = Timeout.InfiniteTimeSpan,

            };

    }

    private sealed class FakeAttachmentRevealLauncher : IAttachmentRevealLauncher
    {

        public int Attempts { get; private set; }

        public string? LastPath { get; private set; }

        public string TryReveal(string absolutePath, out bool started)
        {

            Attempts++;

            LastPath = absolutePath;

            started = true;

            return $"Revealed: {absolutePath}";

        }

    }

    private sealed class TestHomeScope : IDisposable
    {

        private readonly string? _originalAspNetCoreEnvironment;

        private readonly string? _originalDotnetEnvironment;

        private readonly string? _originalTestHome;

        public TestHomeScope()
        {

            _originalAspNetCoreEnvironment =
                global::System.Environment.GetEnvironmentVariable(
                    "ASPNETCORE_ENVIRONMENT");

            _originalDotnetEnvironment =
                global::System.Environment.GetEnvironmentVariable(
                    "DOTNET_ENVIRONMENT");

            _originalTestHome =
                global::System.Environment.GetEnvironmentVariable(
                    "ARCANUM_TEST_HOME");

            Root = Path.Combine(
                Path.GetTempPath(),
                "arcanum-attachment-reveal-tests",
                Guid.NewGuid().ToString("N"));

            global::System.Environment.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                "Testing");

            global::System.Environment.SetEnvironmentVariable(
                "DOTNET_ENVIRONMENT",
                "Testing");

            global::System.Environment.SetEnvironmentVariable(
                "ARCANUM_TEST_HOME",
                Root);

        }

        public string Root { get; }

        public string ResolveAttachmentPath(string relativePath) =>
            Path.GetFullPath(
                Path.Combine(
                    ArcanumPaths.AttachmentsDirectory,
                    relativePath));

        public void Dispose()
        {

            global::System.Environment.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                _originalAspNetCoreEnvironment);

            global::System.Environment.SetEnvironmentVariable(
                "DOTNET_ENVIRONMENT",
                _originalDotnetEnvironment);

            global::System.Environment.SetEnvironmentVariable(
                "ARCANUM_TEST_HOME",
                _originalTestHome);

            if (Directory.Exists(Root))
            {

                Directory.Delete(Root, recursive: true);

            }

        }

    }

    private sealed class RecordingHandler(
        bool failFirstPing = false,
        string? attachmentListEnvelope = null,
        byte[]? attachmentContent = null) : HttpMessageHandler
    {

        private int _pingCount;

        private readonly string _attachmentListEnvelope =
            attachmentListEnvelope ?? AttachmentListEnvelope;

        private readonly byte[] _attachmentContent =
            attachmentContent ?? Encoding.UTF8.GetBytes("attachment-content");

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            byte[] bytes = request.Content is null
                ? []
                : await request.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);

            RecordedRequest recorded = new(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                Encoding.UTF8.GetString(bytes));

            Requests.Add(recorded);

            if (recorded.Path == "/api/perception/chronosync")
            {

                return JsonResponse(
                    """
                    {"data":{"previousSnapshotTime":null,"newThreads":[],"missingThreads":[],"domainChanged":false,"previousDomain":null},"isSuccess":true,"error":null,"traceId":"test"}
                    """);

            }

            if (recorded.Path == "/api/intelligence/ping-stream")
            {

                _pingCount++;

                if (failFirstPing && _pingCount == 1)
                {

                    return NdjsonResponse(
                        SerializeFrames(
                            new IntelligenceEvent(
                                IntelligenceEventType.Error,
                                "simulated failed turn")));

                }

                return NdjsonResponse(
                    SerializeFrames(
                        new IntelligenceEvent(
                            IntelligenceEventType.Token,
                            string.Empty,
                            "ok"),
                        new IntelligenceEvent(
                            IntelligenceEventType.Result,
                            "ok",
                            "ok")));

            }

            if (recorded.Path == "/api/mcp")
            {

                return JsonResponse(
                    """
                    {"data":[],"isSuccess":true,"error":null,"traceId":"test"}
                    """);

            }

            if (recorded.Path == "/api/workspaces")
            {

                return JsonResponse(WorkspaceListEnvelope);

            }

            if (recorded.Path == "/api/workspaces/ws-server")
            {

                return JsonResponse(WorkspaceEnvelope);

            }

            if (recorded.Path.EndsWith("/refresh", StringComparison.Ordinal))
            {

                return JsonResponse(RefreshEnvelope);

            }

            if (recorded.Path.EndsWith("/content", StringComparison.Ordinal))
            {

                HttpResponseMessage response = new(HttpStatusCode.OK)
                {

                    Content = new ByteArrayContent(_attachmentContent),

                };

                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

                response.Content.Headers.ContentDisposition =
                    new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                    {

                        FileNameStar = "notes.txt",

                    };

                return response;

            }

            if (recorded.Path.EndsWith("/context-pins", StringComparison.Ordinal))
            {

                return request.Method == HttpMethod.Get
                    ? JsonResponse(PinListEnvelope)
                    : JsonResponse(PinEnvelope);

            }

            if (request.Method == HttpMethod.Delete
                && recorded.Path.Contains("/context-pins/", StringComparison.Ordinal))
            {

                return new HttpResponseMessage(HttpStatusCode.NoContent);

            }

            if (recorded.Path.Contains("/attachments", StringComparison.Ordinal))
            {

                return request.Method == HttpMethod.Get
                    ? JsonResponse(_attachmentListEnvelope)
                    : JsonResponse(AttachmentEnvelope);

            }

            return JsonResponse(
                """
                {"data":null,"isSuccess":false,"error":{"code":"Test.NotFound","message":"No test response."},"traceId":"test"}
                """,
                HttpStatusCode.NotFound);

        }

    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string ContentType,
        string Body);

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class FakeContextResolver : ICliInferenceContextResolver
    {

        public Task<CliInferenceContextResult> ResolveAsync(
            CliInferenceContextRequest request,
            CancellationToken cancellationToken)
        {

            Guid? sessionId = Guid.TryParse(request.Session, out Guid parsed)
                ? parsed
                : null;

            CliEffectiveContext context = CliContextPrecedence.Resolve(
                new CliContextResolutionRequest(
                    null,
                    request.Workspace ?? request.CurrentDirectory,
                    request.Model,
                    sessionId,
                    CliContextDocument.Empty,
                    null,
                    null,
                    null,
                    null,
                    request.NoContext));

            return Task.FromResult(
                CliInferenceContextResult.Success(context, []));

        }

    }

    private sealed class FakeEye : IEyeOfTheWorld
    {

        public Task<PatternSnapshot> PerceivePatternAsync(
            string directoryPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new PatternSnapshot(
                    DomainType.Unknown,
                    directoryPath,
                    []));

    }

    private sealed class NoopGrimoireInitialization :
        IGrimoireCliInitialization,
        IServiceProvider
    {

        public Task<T> RunExclusiveAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) => operation(this, cancellationToken);

        public Task<T> RunExclusiveWithBootstrapAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) => operation(this, cancellationToken);

        public object? GetService(Type serviceType) => null;

    }

    private sealed class NoopChronosyncEngine : IChronosyncEngine
    {

        public Task<ChronosyncReport> AnalyzeAndSyncAsync(
            PatternSnapshot currentSnapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new ChronosyncReport(
                    null,
                    [],
                    [],
                    false));

    }

    private sealed class NoopServeLauncher : IArcanumServeLauncher
    {

        public Task<ServeLaunchResult> EnsureRunningAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ServeLaunchResult(
                    ServeLaunchStatus.AlreadyRunning,
                    HealthProbeState.Healthy,
                    TimeSpan.Zero,
                    null,
                    null));

    }

    private const string WorkspaceListEnvelope =
        """
        {"data":[{"id":"ws-server","name":"server","path":"/srv/workspace","type":"custom","registeredAt":"2026-08-01T12:00:00Z","persisted":true}],"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string WorkspaceEnvelope =
        """
        {"data":{"id":"ws-server","name":"server","path":"/srv/workspace","type":"custom","registeredAt":"2026-08-01T12:00:00Z","persisted":true},"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string AttachmentEnvelope =
        """
        {"data":{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","logicalKey":"stdin","originalFileName":"stdin.txt","version":1,"relativePath":"session/stdin/v1/stdin.txt","mimeType":"text/plain","byteLength":31,"kind":"Text","contentSha256":"abc123","createdAt":"2026-08-01T12:00:00Z","sourceKind":0,"sourceWorkspaceIdentity":null,"sourceRelativePath":null,"isRefreshable":false,"sourceStatus":0,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"NotEligible"},"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string AttachmentListEnvelope =
        """
        {"data":[{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","logicalKey":"stdin","originalFileName":"stdin.txt","version":1,"relativePath":"session/stdin/v1/stdin.txt","mimeType":"text/plain","byteLength":31,"kind":"Text","contentSha256":"abc123","createdAt":"2026-08-01T12:00:00Z","sourceKind":0,"sourceWorkspaceIdentity":null,"sourceRelativePath":null,"isRefreshable":false,"sourceStatus":0,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"NotEligible","sessionId":"11111111-2222-3333-4444-555555555555"}],"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string VersionedAttachmentListEnvelope =
        """
        {"data":[{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","logicalKey":"notes","originalFileName":"notes-v1.txt","version":1,"relativePath":"session/notes/v1/notes-v1.txt","mimeType":"text/plain","byteLength":11,"kind":"Text","contentSha256":"version-one","createdAt":"2026-08-01T10:00:00Z","sourceKind":0,"sourceWorkspaceIdentity":null,"sourceRelativePath":null,"isRefreshable":false,"sourceStatus":0,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"Indexed","sessionId":"11111111-2222-3333-4444-555555555555"},{"id":"01234567-89ab-cdef-0123-456789abcdef","logicalKey":"notes","originalFileName":"notes.txt","version":2,"relativePath":"session/notes/v2/notes.txt","mimeType":"text/plain","byteLength":22,"kind":"Text","contentSha256":"version-two","createdAt":"2026-08-01T11:00:00Z","sourceKind":1,"sourceWorkspaceIdentity":"workspace","sourceRelativePath":"notes.txt","isRefreshable":true,"sourceStatus":1,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":"version-two","lastObservedSourceWriteTime":"2026-08-01T11:00:00Z","lastObservedSourceByteLength":22,"indexingStatus":"Indexed","sessionId":"11111111-2222-3333-4444-555555555555"}],"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string CaseDistinctAttachmentListEnvelope =
        """
        {"data":[{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","logicalKey":"Notes","originalFileName":"Notes-v1.txt","version":1,"relativePath":"session/Notes/v1/Notes-v1.txt","mimeType":"text/plain","byteLength":11,"kind":"Text","contentSha256":"upper-version-one","createdAt":"2026-08-01T10:00:00Z","sourceKind":0,"sourceWorkspaceIdentity":null,"sourceRelativePath":null,"isRefreshable":false,"sourceStatus":0,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"Indexed","sessionId":"11111111-2222-3333-4444-555555555555"},{"id":"aaaaaaaa-bbbb-cccc-dddd-ffffffffffff","logicalKey":"Notes","originalFileName":"Notes-v2.txt","version":2,"relativePath":"session/Notes/v2/Notes-v2.txt","mimeType":"text/plain","byteLength":22,"kind":"Text","contentSha256":"upper-version-two","createdAt":"2026-08-01T11:00:00Z","sourceKind":0,"sourceWorkspaceIdentity":null,"sourceRelativePath":null,"isRefreshable":false,"sourceStatus":0,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"Indexed","sessionId":"11111111-2222-3333-4444-555555555555"},{"id":"bbbbbbbb-cccc-dddd-eeee-ffffffffffff","logicalKey":"notes","originalFileName":"notes-v1.txt","version":1,"relativePath":"session/notes/v1/notes-v1.txt","mimeType":"text/plain","byteLength":33,"kind":"Text","contentSha256":"lower-version-one","createdAt":"2026-08-01T12:00:00Z","sourceKind":0,"sourceWorkspaceIdentity":null,"sourceRelativePath":null,"isRefreshable":false,"sourceStatus":0,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"Indexed","sessionId":"11111111-2222-3333-4444-555555555555"},{"id":"01234567-89ab-cdef-0123-456789abcdef","logicalKey":"notes","originalFileName":"notes-v3.txt","version":3,"relativePath":"session/notes/v3/notes-v3.txt","mimeType":"text/plain","byteLength":44,"kind":"Text","contentSha256":"lower-version-three","createdAt":"2026-08-01T13:00:00Z","sourceKind":0,"sourceWorkspaceIdentity":null,"sourceRelativePath":null,"isRefreshable":false,"sourceStatus":0,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"Indexed","sessionId":"11111111-2222-3333-4444-555555555555"}],"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string EscapingAttachmentListEnvelope =
        """
        {"data":[{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","logicalKey":"unsafe","originalFileName":"unsafe.txt","version":1,"relativePath":"../../escape.txt","mimeType":"text/plain","byteLength":11,"kind":"Text","contentSha256":"unsafe","createdAt":"2026-08-01T10:00:00Z","sourceKind":0,"sourceWorkspaceIdentity":null,"sourceRelativePath":null,"isRefreshable":false,"sourceStatus":0,"sourceDiagnosticReason":null,"lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"NotEligible","sessionId":"11111111-2222-3333-4444-555555555555"}],"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string DiagnosticAttachmentListEnvelope =
        """
        {"data":[{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","logicalKey":"notes","originalFileName":"notes.txt","version":1,"relativePath":"session/notes/v1/notes.txt","mimeType":"text/plain","byteLength":11,"kind":"Text","contentSha256":"notes","createdAt":"2026-08-01T10:00:00Z","sourceKind":1,"sourceWorkspaceIdentity":"workspace","sourceRelativePath":"notes.txt","isRefreshable":false,"sourceStatus":3,"sourceDiagnosticReason":"Missing at /srv/private/notes.txt\r\nretry later.","lastObservedSourceContentSha256":null,"lastObservedSourceWriteTime":null,"lastObservedSourceByteLength":null,"indexingStatus":"Stale","sessionId":"11111111-2222-3333-4444-555555555555"}],"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string RefreshEnvelope =
        """
        {"data":{"attachmentId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","logicalKey":"stdin","version":2,"newVersionCreated":true,"queuedForInjection":false,"sanitizedSourcePath":"workspace://stdin.txt","contentSha256":"refreshed","byteLength":31,"sourceFreshnessTimestamp":"2026-08-01T12:30:00Z"},"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string PinEnvelope =
        """
        {"data":{"id":"99999999-8888-7777-6666-555555555555","sessionId":"11111111-2222-3333-4444-555555555555","kind":4,"targetIdentifier":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","displayLabel":"stdin","contentVersion":"abc123","createdAt":"2026-08-01T12:00:00Z","updatedAt":"2026-08-01T12:00:00Z"},"isSuccess":true,"error":null,"traceId":"test"}
        """;

    private const string PinListEnvelope =
        """
        {"data":[{"id":"99999999-8888-7777-6666-555555555555","sessionId":"11111111-2222-3333-4444-555555555555","kind":4,"targetIdentifier":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","displayLabel":"stdin","contentVersion":"abc123","createdAt":"2026-08-01T12:00:00Z","updatedAt":"2026-08-01T12:00:00Z"}],"isSuccess":true,"error":null,"traceId":"test"}
        """;

}
