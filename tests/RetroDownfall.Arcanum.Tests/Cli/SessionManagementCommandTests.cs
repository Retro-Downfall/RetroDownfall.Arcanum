using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class SessionManagementCommandTests
{

    [Fact]

    public void Session_help_exposes_complete_lifecycle_command_family()
    {

        CliTestResult result = RunCommand(new RecordingHandler(), ["session", "--help"]);

        Assert.Equal(0, result.ExitCode);

        // Management only. Starting or continuing a turn lives on the inference entries
        // (bare `arcanum` and `arcanum run -c/-r`), and live streaming lives on `watch session`.
        string[] commands =
        [
            "list",
            "show",
            "entries",
            "fork",
            "rename",
            "archive",
            "export",
            "rest",
            "attachments",
            "delete-entry",
            "pin-entry",
            "unpin-entry",
            "compact",
            "divine",
        ];

        foreach (string command in commands)
        {

            Assert.Contains(command, result.Output, StringComparison.Ordinal);

        }

    }

    [Fact]

    public void Session_list_passes_all_filters_and_emits_structured_json()
    {

        Guid campaignId = Guid.NewGuid();

        SessionQueryResult payload = new(
            [new SessionSummaryDto(Guid.NewGuid(), campaignId, "Quest", "active", 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
            null,
            false);

        RecordingHandler handler = new(_ => CreateResponse(
            ApiResponse<SessionQueryResult>.FromResult(Result<SessionQueryResult>.Success(payload)),
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult));

        CliTestResult result = RunCommand(
            handler,
            [
                "--json",
                "session",
                "list",
                "--campaign",
                campaignId.ToString("D"),
                "--status",
                "active",
                "--search",
                "quest",
                "--model",
                "gpt-5",
                "--from",
                "2026-07-01T00:00:00Z",
                "--to",
                "2026-07-31T23:59:59Z",
            ]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        string query = request.RequestUri!.Query;

        Assert.Contains($"campaignId={campaignId:D}", query, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("status=active", query, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("search=quest", query, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("model=gpt-5", query, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("from=", query, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("to=", query, StringComparison.OrdinalIgnoreCase);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

    }

    [Fact]

    public void Session_show_combines_detail_and_attachment_count_as_json()
    {

        Guid sessionId = Guid.NewGuid();

        SessionDetailDto detail = new(
            sessionId,
            null,
            "Quest",
            "archived",
            7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "summary",
            123,
            null);

        SessionAttachmentDto[] attachments =
        [
            new(Guid.NewGuid(), "map", "map.png", 1, "relative/map.png", "image/png", 42, SessionAttachmentKind.Image, "abc", DateTimeOffset.UtcNow),
        ];

        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath.EndsWith("/attachments", StringComparison.Ordinal)
            ? CreateResponse(
                ApiResponse<SessionAttachmentDto[]>.FromResult(Result<SessionAttachmentDto[]>.Success(attachments)),
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray)
            : CreateResponse(
                ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(detail)),
                ArcanumJsonContext.Default.ApiResponseSessionDetailDto));

        CliTestResult result = RunCommand(handler, ["--json", "session", "show", sessionId.ToString("D")]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(1, document.RootElement.GetProperty("attachmentCount").GetInt32());

        Assert.Equal(123, document.RootElement.GetProperty("totalTokensUsed").GetInt64());

        Assert.True(document.RootElement.TryGetProperty("totalCostUsd", out _));

        Assert.True(document.RootElement.TryGetProperty("forkedFromSessionId", out _));

    }

    [Fact]

    public void Session_title_resolution_includes_archived_sessions()
    {

        Guid sessionId = Guid.NewGuid();

        SessionSummaryDto summary = new(
            sessionId,
            null,
            "Archived quest",
            "archived",
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        SessionDetailDto detail = new(
            sessionId,
            null,
            summary.Title,
            summary.Status,
            summary.EntryCount,
            summary.CreatedAt,
            summary.UpdatedAt,
            null,
            0);

        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/sessions" when request.RequestUri.Query.Contains("status=all", StringComparison.OrdinalIgnoreCase) =>
                CreateResponse(
                    ApiResponse<SessionQueryResult>.FromResult(
                        Result<SessionQueryResult>.Success(new SessionQueryResult([summary], null, false))),
                    ArcanumJsonContext.Default.ApiResponseSessionQueryResult),
            "/api/sessions" => CreateResponse(
                ApiResponse<SessionQueryResult>.FromResult(
                    Result<SessionQueryResult>.Success(new SessionQueryResult([], null, false))),
                ArcanumJsonContext.Default.ApiResponseSessionQueryResult),
            var path when path.EndsWith("/attachments", StringComparison.Ordinal) => CreateResponse(
                ApiResponse<SessionAttachmentDto[]>.FromResult(Result<SessionAttachmentDto[]>.Success([])),
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray),
            _ => CreateResponse(
                ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(detail)),
                ArcanumJsonContext.Default.ApiResponseSessionDetailDto),
        });

        CliTestResult result = RunCommand(handler, ["session", "show", "Archived quest"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(3, handler.Requests.Count);

    }

    [Theory]

    [InlineData("entries", "GET", "/entries")]

    [InlineData("attachments", "GET", "/attachments")]

    [InlineData("rest", "POST", "/rest")]

    [InlineData("compact", "POST", "/compact")]

    public void Session_commands_reuse_existing_lifecycle_endpoints(
        string command,
        string method,
        string suffix)
    {

        Guid sessionId = Guid.NewGuid();

        RecordingHandler handler = new(request => ResponseFor(request, sessionId));

        CliTestResult result = RunCommand(handler, ["--json", "session", command, sessionId.ToString("D")]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(method, request.Method.Method);

        Assert.Equal($"/api/sessions/{sessionId:D}{suffix}", request.RequestUri!.AbsolutePath);

    }

    [Fact]

    public void Session_fork_rename_archive_and_export_use_server_contracts()
    {

        Guid sessionId = Guid.NewGuid();

        RecordingHandler handler = new(request => ResponseFor(request, sessionId));

        Assert.Equal(0, RunCommand(handler, ["session", "fork", sessionId.ToString("D"), "--title", "Branch"]).ExitCode);

        Assert.Equal(0, RunCommand(handler, ["session", "rename", sessionId.ToString("D"), "--title", "Renamed"]).ExitCode);

        Assert.Equal(0, RunCommand(handler, ["session", "archive", sessionId.ToString("D")]).ExitCode);

        Assert.Equal(0, RunCommand(handler, ["--json", "session", "export", sessionId.ToString("D"), "--format", "markdown"]).ExitCode);

        Assert.Collection(
            handler.Requests,
            request =>
            {

                Assert.Equal(HttpMethod.Post, request.Method);

                Assert.EndsWith("/fork", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);

            },
            request =>
            {

                Assert.Equal(HttpMethod.Patch, request.Method);

                Assert.Equal($"/api/sessions/{sessionId:D}", request.RequestUri!.AbsolutePath);

            },
            request =>
            {

                Assert.Equal(HttpMethod.Delete, request.Method);

                Assert.Equal($"/api/sessions/{sessionId:D}", request.RequestUri!.AbsolutePath);

            },
            request =>
            {

                Assert.Equal(HttpMethod.Get, request.Method);

                Assert.EndsWith("/export", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);

                Assert.Equal("?format=markdown", request.RequestUri.Query);

            });

    }

    [Theory]

    [InlineData("delete-entry", "DELETE", "")]

    [InlineData("pin-entry", "POST", "/pin")]

    [InlineData("unpin-entry", "DELETE", "/pin")]

    public void Entry_management_uses_selected_session_and_preserves_server_feature_gate(
        string command,
        string method,
        string suffix)
    {

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        RecordingHandler handler = new(request => ResponseFor(request, sessionId));

        CliTestResult result = RunCommand(
            handler,
            ["--yes", "session", command, entryId.ToString("D"), "--session", sessionId.ToString("D")]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(method, request.Method.Method);

        Assert.Equal($"/api/sessions/{sessionId:D}/entries/{entryId:D}{suffix}", request.RequestUri!.AbsolutePath);

    }

    /// <summary>
    /// The management tree no longer starts a turn. Continuation moved to the inference entry, and
    /// every spelling there accepts a GUID, an exact title, or a unique title prefix.
    /// </summary>
    [Fact]
    public void Session_continuation_lives_on_the_inference_entry()
    {

        RecordingHandler handler = new();

        CliTestResult run = RunCommand(handler, ["run", "--help"]);

        Assert.Equal(0, run.ExitCode);

        Assert.Contains("--session", run.Output, StringComparison.Ordinal);

        Assert.Contains("--continue", run.Output, StringComparison.Ordinal);

        Assert.Contains("--resume", run.Output, StringComparison.Ordinal);

        Assert.Contains("title", run.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Session_watch_reads_server_sent_entries_as_newline_delimited_json()
    {

        Guid sessionId = Guid.NewGuid();

        EntryDto entry = new(
            Guid.NewGuid(),
            sessionId,
            "assistant",
            "A live answer",
            null,
            null,
            DateTimeOffset.UtcNow);

        string entryJson = JsonSerializer.Serialize(entry, ArcanumJsonContext.Default.EntryDto);

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"data: {entryJson}\n\ndata: {{\"type\":\"live\"}}\n\ndata: [DONE]\n\n"),
        });

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "session", sessionId.ToString("D")]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/sessions/{sessionId:D}/stream", request.RequestUri!.AbsolutePath);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(entry.Id, document.RootElement.GetProperty("id").GetGuid());

    }

    [Fact]

    public void Session_memory_errors_keep_api_code_visible_and_actionable()
    {

        Guid sessionId = Guid.NewGuid();

        Error error = new(
            ErrorCodes.Session.MemoryManagementDisabled,
            "Memory management is disabled.");

        RecordingHandler handler = new(_ => CreateResponse(
            ApiResponse<CompactResult>.FromResult(Result<CompactResult>.Failure(error)),
            ArcanumJsonContext.Default.ApiResponseCompactResult,
            HttpStatusCode.BadRequest));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "session", "compact", sessionId.ToString("D")]);

        Assert.Equal(1, result.ExitCode);

        // The code and the message stay visible and actionable, on the diagnostic stream. They used
        // to land on stdout, which under --output-format json meant the API's diagnostic was served
        // to the consumer as the document's own payload with stderr left empty.
        Assert.Contains(error.Code, result.Error, StringComparison.Ordinal);

        Assert.Contains(error.Message, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(error.Code, result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Session_watch_preserves_since_entry_api_error_code()
    {

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        Error error = new(
            ErrorCodes.Session.EntryNotFound,
            "No entry exists with that id in this session.");

        RecordingHandler handler = new(_ => CreateResponse(
            ApiResponse<EntryDto>.FromResult(Result<EntryDto>.Failure(error)),
            ArcanumJsonContext.Default.ApiResponseEntryDto,
            HttpStatusCode.NotFound));

        CliTestResult result = RunCommand(
            handler,
            [
                "--json",
                "watch",
                "session",
                sessionId.ToString("D"),
                "--since",
                entryId.ToString("D"),
            ]);

        Assert.Equal(1, result.ExitCode);

        Assert.True(string.IsNullOrWhiteSpace(result.Output));

        Assert.Contains(error.Code, result.Error, StringComparison.Ordinal);

        Assert.Contains(error.Message, result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Delete_entry_requires_confirmation_before_sending_request()
    {

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        RecordingHandler handler = new(request => ResponseFor(request, sessionId));

        CliTestResult result = RunCommand(
            handler,
            ["session", "delete-entry", entryId.ToString("D"), "--session", sessionId.ToString("D")]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);

    }

    private static HttpResponseMessage ResponseFor(HttpRequestMessage request, Guid sessionId)
    {

        string path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("/entries", StringComparison.Ordinal))
        {

            return CreateResponse(
                ApiResponse<EntryDto[]>.FromResult(Result<EntryDto[]>.Success([])),
                ArcanumJsonContext.Default.ApiResponseEntryDtoArray);

        }

        if (path.EndsWith("/attachments", StringComparison.Ordinal))
        {

            return CreateResponse(
                ApiResponse<SessionAttachmentDto[]>.FromResult(Result<SessionAttachmentDto[]>.Success([])),
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        }

        if (path.EndsWith("/compact", StringComparison.Ordinal))
        {

            return CreateResponse(
                ApiResponse<CompactResult>.FromResult(Result<CompactResult>.Success(new CompactResult(100, 40, 3))),
                ArcanumJsonContext.Default.ApiResponseCompactResult);

        }

        if (path.EndsWith("/fork", StringComparison.Ordinal))
        {

            SessionDetailDto fork = new(
                Guid.NewGuid(),
                null,
                "Branch",
                "active",
                0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                0,
                sessionId);

            return CreateResponse(
                ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(fork)),
                ArcanumJsonContext.Default.ApiResponseSessionDetailDto,
                HttpStatusCode.Created);

        }

        if (path.EndsWith("/export", StringComparison.Ordinal))
        {

            SessionExportResult export = new(sessionId, "markdown", "# Quest", "text/markdown");

            return CreateResponse(
                ApiResponse<SessionExportResult>.FromResult(Result<SessionExportResult>.Success(export)),
                ArcanumJsonContext.Default.ApiResponseSessionExportResult);

        }

        if (request.Method == HttpMethod.Patch)
        {

            SessionDetailDto renamed = new(
                sessionId,
                null,
                "Renamed",
                "active",
                0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                0);

            return CreateResponse(
                ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(renamed)),
                ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        }

        if (path.EndsWith("/rest", StringComparison.Ordinal))
        {

            return CreateResponse(
                ApiResponse<bool>.FromResult(Result<bool>.Success(true)),
                ArcanumJsonContext.Default.ApiResponseBoolean,
                HttpStatusCode.Accepted);

        }

        if (path.Contains("/entries/", StringComparison.Ordinal))
        {

            HttpStatusCode status = request.Method == HttpMethod.Delete && !path.EndsWith("/pin", StringComparison.Ordinal)
                ? HttpStatusCode.NoContent
                : HttpStatusCode.OK;

            return status == HttpStatusCode.NoContent
                ? new HttpResponseMessage(status)
                : CreateResponse(
                    ApiResponse<bool>.FromResult(Result<bool>.Success(true)),
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    status);

        }

        if (request.Method == HttpMethod.Delete)
        {

            return new HttpResponseMessage(HttpStatusCode.NoContent);

        }

        throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");

    }

    /// <summary>
    /// The payload/diagnostic split is absolute. A failure rendered through the global Spectre
    /// console lands on stdout, so `arcanum session list &gt; sessions.txt 2&gt; errors.log` used to
    /// write the error into the data file and leave the log empty — a wrapper that inspects only
    /// stderr then reports a clean run over a file holding a diagnostic instead of a session table.
    /// </summary>
    [Fact]

    public void Session_list_reports_a_failure_on_the_diagnostic_stream_only()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SessionQueryResult>(
                null,
                false,
                new Error("Grimoire.Unavailable", "The Grimoire is unavailable.")),
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult,
            HttpStatusCode.InternalServerError));

        CliTestResult result = RunCommand(handler, ["session", "list"]);

        Assert.NotEqual(0, result.ExitCode);

        Assert.False(
            string.IsNullOrWhiteSpace(result.Error),
            "The failure must be reported on stderr.");

        Assert.True(
            string.IsNullOrWhiteSpace(result.Output),
            $"stdout is the payload stream and must stay clean, got: {result.Output}");

    }

    private static CliTestResult RunCommand(RecordingHandler handler, string[] args)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        return CliTestHarness.Run(services, args);

    }

    private static HttpResponseMessage CreateResponse<T>(
        ApiResponse<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo,
        HttpStatusCode status = HttpStatusCode.OK)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(json),
        };

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class FakeHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {

                snapshot.Content = new ByteArrayContent(
                    request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult());

            }

            Requests.Add(snapshot);

            return Task.FromResult(responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));

        }

    }

}
