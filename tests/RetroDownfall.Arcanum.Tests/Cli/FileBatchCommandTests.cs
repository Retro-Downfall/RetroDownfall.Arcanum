using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class FileBatchCommandTests
{

    private const string FileId = "file-11111111111111111111111111111111";

    private const string BatchId = "batch_22222222222222222222222222222222";

    [Fact]

    public void Help_exposes_complete_file_and_batch_command_families()
    {

        CliTestResult file = RunCommand(new RecordingHandler(), ["file", "--help"]);

        CliTestResult batch = RunCommand(new RecordingHandler(), ["batch", "--help"]);

        Assert.Equal(0, file.ExitCode);

        Assert.Equal(0, batch.ExitCode);

        foreach (string command in new[] { "upload", "list", "show", "download", "delete" })
        {

            Assert.Contains(command, file.Output, StringComparison.Ordinal);

        }

        foreach (string command in new[] { "create", "list", "show", "wait", "cancel", "reset", "output", "errors" })
        {

            Assert.Contains(command, batch.Output, StringComparison.Ordinal);

        }

    }

    [Fact]

    public void File_upload_streams_multipart_and_writes_bare_openai_json()
    {

        string path = WriteJsonl(ValidJsonl);

        try
        {

            RecordingHandler handler = new(
                _ => JsonResponse(FileJson));

            CliTestResult result = RunCommand(
                handler,
                ["--json", "file", "upload", path]);

            Assert.Equal(0, result.ExitCode);

            RecordedRequest request = Assert.Single(handler.Requests);

            Assert.Equal(HttpMethod.Post, request.Method);

            Assert.Equal("/v1/files", request.Path);

            Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.Ordinal);

            Assert.Contains("name=purpose", request.Body, StringComparison.Ordinal);

            Assert.Contains("batch", request.Body, StringComparison.Ordinal);

            Assert.Contains("batch-input.jsonl", request.Body, StringComparison.Ordinal);

            Assert.StartsWith("{", result.Output.Trim(), StringComparison.Ordinal);

            Assert.Contains($"\"id\":\"{FileId}\"", result.Output, StringComparison.Ordinal);

            Assert.DoesNotContain("isSuccess", result.Output, StringComparison.Ordinal);

        }
        finally
        {

            File.Delete(path);

        }

    }

    [Fact]

    public void Batch_create_accepts_existing_uploaded_file_id()
    {

        RecordingHandler handler = new(
            _ => JsonResponse(BatchJson("validating", 0, 0, 0)));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "batch", "create", FileId]);

        Assert.Equal(0, result.ExitCode);

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/v1/batches", request.Path);

        Assert.Contains($"\"input_file_id\":\"{FileId}\"", request.Body, StringComparison.Ordinal);

        Assert.Contains("\"endpoint\":\"/v1/chat/completions\"", request.Body, StringComparison.Ordinal);

        Assert.Contains($"\"id\":\"{BatchId}\"", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Batch_create_rejects_obviously_invalid_local_wrapper_before_upload()
    {

        string path = WriteJsonl(
            """
            {"custom_id":"request-1","method":"GET","url":"/v1/chat/completions","body":{}}
            """);

        try
        {

            RecordingHandler handler = new();

            CliTestResult result = RunCommand(
                handler,
                ["batch", "create", path]);

            Assert.Equal(1, result.ExitCode);

            Assert.Empty(handler.Requests);

            Assert.Contains("line 1", result.Error, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("method", result.Error, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            File.Delete(path);

        }

    }

    [Fact]

    public void Batch_create_from_jsonl_uploads_then_creates_in_one_command()
    {

        string path = WriteJsonl(ValidJsonl);

        try
        {

            RecordingHandler handler = new(
                request => request.Path switch
                {
                    "/v1/files" => JsonResponse(FileJson),
                    "/v1/batches" => JsonResponse(BatchJson("validating", 0, 0, 0)),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                });

            CliTestResult result = RunCommand(
                handler,
                ["batch", "create", path]);

            Assert.Equal(0, result.ExitCode);

            Assert.Collection(
                handler.Requests,
                request => Assert.Equal("/v1/files", request.Path),
                request =>
                {
                    Assert.Equal("/v1/batches", request.Path);

                    Assert.Contains(FileId, request.Body, StringComparison.Ordinal);
                });

            Assert.Contains(BatchId, result.Output, StringComparison.Ordinal);

            Assert.Contains("validating", result.Output, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            File.Delete(path);

        }

    }

    [Fact]

    public void Batch_create_prefers_an_existing_local_path_that_starts_with_file_prefix()
    {

        string directory = CreateTempDirectory();

        string originalDirectory = global::System.Environment.CurrentDirectory;

        try
        {

            global::System.Environment.CurrentDirectory = directory;

            File.WriteAllText("file-input.jsonl", ValidJsonl);

            RecordingHandler handler = new(
                request => request.Path switch
                {
                    "/v1/files" => JsonResponse(FileJson),
                    "/v1/batches" => JsonResponse(BatchJson("validating", 0, 0, 0)),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                });

            CliTestResult result = RunCommand(
                handler,
                ["batch", "create", "file-input.jsonl"]);

            Assert.Equal(0, result.ExitCode);

            Assert.Collection(
                handler.Requests,
                request => Assert.Equal("/v1/files", request.Path),
                request => Assert.Equal("/v1/batches", request.Path));

        }
        finally
        {

            global::System.Environment.CurrentDirectory = originalDirectory;

            Directory.Delete(directory, recursive: true);

        }

    }

    [Fact]

    public void Batch_wait_polls_until_terminal_and_displays_request_counts()
    {

        Queue<HttpResponseMessage> responses = new(
            [
                JsonResponse(BatchJson("validating", 3, 0, 0)),
                JsonResponse(BatchJson("in_progress", 3, 1, 0)),
                JsonResponse(BatchJson("completed", 3, 2, 1)),
            ]);

        RecordingHandler handler = new(
            _ => responses.Dequeue());

        CliTestResult result = RunCommand(
            handler,
            ["batch", "wait", BatchId, "--poll-interval", "1"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(3, handler.Requests.Count);

        Assert.All(
            handler.Requests,
            request => Assert.Equal($"/v1/batches/{BatchId}", request.Path));

        Assert.Contains("completed", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("2 completed", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("1 failed", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Batch_watch_json_parse_failure_retains_the_global_error_envelope()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            [
                "--json",
                "batch",
                "wait",
                BatchId,
                "--poll-interval",
                "not-a-number",
            ]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.False(string.IsNullOrWhiteSpace(result.Output));

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(
            "The command line is invalid.",
            document.RootElement.GetProperty("error").GetString());

        Assert.Equal(
            (int)CliExitCode.ConfigurationError,
            document.RootElement.GetProperty("exitCode").GetInt32());

        Assert.Contains("invalid", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Batch_preflight_reports_the_malformed_jsonl_line_number()
    {

        string path = WriteJsonl(ValidJsonl + global::System.Environment.NewLine + "{");

        try
        {

            RecordingHandler handler = new();

            CliTestResult result = RunCommand(
                handler,
                ["batch", "create", path]);

            Assert.Equal(1, result.ExitCode);

            Assert.Empty(handler.Requests);

            Assert.Contains("line 2", result.Error, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            File.Delete(path);

        }

    }

    [Fact]

    public void File_download_ignores_unsafe_server_path_components_and_streams_content()
    {

        string directory = CreateTempDirectory();

        string originalDirectory = global::System.Environment.CurrentDirectory;

        try
        {

            global::System.Environment.CurrentDirectory = directory;

            RecordingHandler handler = new(
                request => request.Path.EndsWith("/content", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {

                        Content = new StringContent("downloaded-jsonl", Encoding.UTF8, "application/jsonl"),

                    }
                    : JsonResponse(FileJson.Replace("batch-input.jsonl", "../../unsafe.jsonl", StringComparison.Ordinal)));

            CliTestResult result = RunCommand(
                handler,
                ["file", "download", FileId]);

            Assert.Equal(0, result.ExitCode);

            string destination = Path.Combine(directory, "unsafe.jsonl");

            Assert.Equal("downloaded-jsonl", File.ReadAllText(destination));

            Assert.DoesNotContain("..", result.Output, StringComparison.Ordinal);

            Assert.Collection(
                handler.Requests,
                request => Assert.Equal($"/v1/files/{FileId}", request.Path),
                request => Assert.Equal($"/v1/files/{FileId}/content", request.Path));

        }
        finally
        {

            global::System.Environment.CurrentDirectory = originalDirectory;

            Directory.Delete(directory, recursive: true);

        }

    }

    [Fact]

    public void File_download_existing_destination_fails_closed_without_yes()
    {

        string directory = CreateTempDirectory();

        string destination = Path.Combine(directory, "existing.jsonl");

        File.WriteAllText(destination, "original");

        try
        {

            RecordingHandler handler = new(_ => JsonResponse(FileJson));

            CliTestResult result = RunCommand(
                handler,
                ["file", "download", FileId, "--output", destination]);

            Assert.Equal(2, result.ExitCode);

            Assert.Equal("original", File.ReadAllText(destination));

            Assert.Single(handler.Requests);

            Assert.Contains("--yes", result.Error, StringComparison.Ordinal);

        }
        finally
        {

            Directory.Delete(directory, recursive: true);

        }

    }

    [Fact]

    public void File_download_existing_destination_overwrites_only_with_yes_after_success()
    {

        string directory = CreateTempDirectory();

        string destination = Path.Combine(directory, "existing.jsonl");

        File.WriteAllText(destination, "original");

        try
        {

            RecordingHandler handler = new(
                request => request.Path.EndsWith("/content", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {

                        Content = new StringContent("replacement", Encoding.UTF8, "application/jsonl"),

                    }
                    : JsonResponse(FileJson));

            CliTestResult result = RunCommand(
                handler,
                ["--yes", "file", "download", FileId, "--output", destination]);

            Assert.Equal(0, result.ExitCode);

            Assert.Equal("replacement", File.ReadAllText(destination));

            Assert.Equal(2, handler.Requests.Count);

        }
        finally
        {

            Directory.Delete(directory, recursive: true);

        }

    }

    [Fact]

    public void Batch_output_resolves_server_artifact_id_and_downloads_jsonl()
    {

        const string OutputFileId = "file-33333333333333333333333333333333";

        string directory = CreateTempDirectory();

        string destination = Path.Combine(directory, "result.jsonl");

        try
        {

            RecordingHandler handler = new(
                request => request.Path.EndsWith("/content", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {

                        Content = new StringContent("{\"result\":true}", Encoding.UTF8, "application/jsonl"),

                    }
                    : JsonResponse(BatchJsonWithArtifacts(OutputFileId, null)));

            CliTestResult result = RunCommand(
                handler,
                ["batch", "output", BatchId, "--output", destination]);

            Assert.Equal(0, result.ExitCode);

            Assert.Equal("{\"result\":true}", File.ReadAllText(destination));

            Assert.Collection(
                handler.Requests,
                request => Assert.Equal($"/v1/batches/{BatchId}", request.Path),
                request => Assert.Equal($"/v1/files/{OutputFileId}/content", request.Path));

        }
        finally
        {

            Directory.Delete(directory, recursive: true);

        }

    }

    [Theory]

    [InlineData("cancel", "/cancel")]

    [InlineData("reset", "/reset")]

    public void Batch_mutations_post_directly_to_server_semantics(
        string command,
        string suffix)
    {

        RecordingHandler handler = new(
            _ => JsonResponse(BatchJson("cancelled", 3, 1, 0)));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "batch", command, BatchId]);

        Assert.Equal(0, result.ExitCode);

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal($"/v1/batches/{BatchId}{suffix}", request.Path);

        Assert.Contains("\"status\":\"cancelled\"", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("isSuccess", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void File_list_show_and_delete_use_openai_routes_and_shapes()
    {

        RecordingHandler listHandler = new(
            _ => JsonResponse($$"""{"data":[{{FileJson}}],"object":"list"}"""));

        CliTestResult list = RunCommand(
            listHandler,
            ["--json", "file", "list", "--purpose", "batch"]);

        Assert.Equal(0, list.ExitCode);

        Assert.Equal("/v1/files", Assert.Single(listHandler.Requests).Path);

        Assert.Contains("\"data\"", list.Output, StringComparison.Ordinal);

        RecordingHandler showHandler = new(_ => JsonResponse(FileJson));

        CliTestResult show = RunCommand(
            showHandler,
            ["--json", "file", "show", FileId]);

        Assert.Equal(0, show.ExitCode);

        Assert.Equal($"/v1/files/{FileId}", Assert.Single(showHandler.Requests).Path);

        RecordingHandler deleteHandler = new(
            _ => JsonResponse($$"""{"id":"{{FileId}}","deleted":true,"object":"file"}"""));

        CliTestResult delete = RunCommand(
            deleteHandler,
            ["--yes", "--json", "file", "delete", FileId]);

        Assert.Equal(0, delete.ExitCode);

        RecordedRequest deleteRequest = Assert.Single(deleteHandler.Requests);

        Assert.Equal(HttpMethod.Delete, deleteRequest.Method);

        Assert.Equal($"/v1/files/{FileId}", deleteRequest.Path);

        Assert.Contains("\"deleted\":true", delete.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Batch_list_and_show_preserve_request_counts_in_json()
    {

        string batch = BatchJson("in_progress", 4, 2, 1);

        RecordingHandler listHandler = new(
            _ => JsonResponse($$"""{"data":[{{batch}}],"has_more":false,"object":"list"}"""));

        CliTestResult list = RunCommand(
            listHandler,
            ["--json", "batch", "list", "--status", "in_progress"]);

        Assert.Equal(0, list.ExitCode);

        Assert.Equal("/v1/batches", Assert.Single(listHandler.Requests).Path);

        Assert.Contains("\"request_counts\"", list.Output, StringComparison.Ordinal);

        RecordingHandler showHandler = new(_ => JsonResponse(batch));

        CliTestResult show = RunCommand(
            showHandler,
            ["--json", "batch", "show", BatchId]);

        Assert.Equal(0, show.ExitCode);

        Assert.Equal($"/v1/batches/{BatchId}", Assert.Single(showHandler.Requests).Path);

        Assert.Contains("\"total\":4", show.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Batch_list_cursor_is_forwarded_and_human_output_prints_exact_continuation()

    {

        string batch = BatchJson("in_progress", 4, 2, 1);

        RecordingHandler handler = new(

            _ => JsonResponse(

                $$"""{"data":[{{batch}}],"has_more":true,"next_cursor":"opaque-next","object":"list"}"""));

        CliTestResult result = RunCommand(

            handler,

            ["batch", "list", "--status", "in_progress", "--cursor", "opaque-current"]);

        Assert.Equal(0, result.ExitCode);

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal("?status=in_progress&after=opaque-current", request.Query);

        Assert.Contains(

            "arcanum batch list --status in_progress --cursor opaque-next",

            result.Output,

            StringComparison.Ordinal);

    }

    [Fact]

    public void Batch_errors_downloads_the_error_artifact_id()
    {

        const string ErrorFileId = "file-44444444444444444444444444444444";

        string directory = CreateTempDirectory();

        string destination = Path.Combine(directory, "errors.jsonl");

        try
        {

            RecordingHandler handler = new(
                request => request.Path.EndsWith("/content", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {

                        Content = new StringContent("{\"line\":2}", Encoding.UTF8, "application/jsonl"),

                    }
                    : JsonResponse(BatchJsonWithArtifacts(null, ErrorFileId)));

            CliTestResult result = RunCommand(
                handler,
                ["--json", "batch", "errors", BatchId, "--output", destination]);

            Assert.Equal(0, result.ExitCode);

            Assert.Equal("{\"line\":2}", File.ReadAllText(destination));

            Assert.Equal($"/v1/files/{ErrorFileId}/content", handler.Requests[1].Path);

            Assert.Contains("\"kind\":\"errors\"", result.Output, StringComparison.Ordinal);

        }
        finally
        {

            Directory.Delete(directory, recursive: true);

        }

    }

    [Fact]

    public void Openai_error_envelope_is_reported_without_native_api_wrapper_assumptions()
    {

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {

                Content = new StringContent(
                    """
                    {"error":{"message":"No such file.","type":"invalid_request_error","code":"not_found","param":"id"}}
                    """,
                    Encoding.UTF8,
                    "application/json"),

            });

        CliTestResult result = RunCommand(
            handler,
            ["file", "show", FileId]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("not_found: No such file.", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("isSuccess", result.Error, StringComparison.Ordinal);

    }

    private static string WriteJsonl(string content)
    {

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-file-batch-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, "batch-input.jsonl");

        File.WriteAllText(path, content);

        return path;

    }

    private static string CreateTempDirectory()
    {

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-file-batch-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        return directory;

    }

    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args)
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
            new FakeSecretStore("test-key"));

        return CliTestHarness.Run(services, args);

    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {

            Content = new StringContent(json, Encoding.UTF8, "application/json"),

        };

    private const string ValidJsonl =
        """
        {"custom_id":"request-1","method":"POST","url":"/v1/chat/completions","body":{"model":"test","messages":[{"role":"user","content":"Hello"}]}}
        """;

    private const string FileJson =
        """
        {"id":"file-11111111111111111111111111111111","bytes":142,"created_at":1785456000,"filename":"batch-input.jsonl","purpose":"batch","object":"file"}
        """;

    private static string BatchJson(
        string status,
        int total,
        int completed,
        int failed) =>
        $$"""
        {"id":"{{BatchId}}","endpoint":"/v1/chat/completions","input_file_id":"{{FileId}}","completion_window":"24h","status":"{{status}}","created_at":1785456000,"request_counts":{"total":{{total}},"completed":{{completed}},"failed":{{failed}}},"output_file_id":null,"error_file_id":null,"completed_at":null,"object":"batch"}
        """;

    private static string BatchJsonWithArtifacts(
        string? outputFileId,
        string? errorFileId) =>
        $$"""
        {"id":"{{BatchId}}","endpoint":"/v1/chat/completions","input_file_id":"{{FileId}}","completion_window":"24h","status":"completed","created_at":1785456000,"request_counts":{"total":1,"completed":1,"failed":0},"output_file_id":{{JsonString(outputFileId)}},"error_file_id":{{JsonString(errorFileId)}},"completed_at":1785456060,"object":"batch"}
        """;

    private static string JsonString(string? value) =>
        value is null ? "null" : $"\"{value}\"";

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class FakeHttpClientFactory(
        RecordingHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001/"),

            };

    }

    private sealed class RecordingHandler(
        Func<RecordedRequest, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            RecordedRequest recorded = new(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                body);

            Requests.Add(recorded);

            return responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(recorded);

        }

    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Query,
        string ContentType,
        string Body);

}
