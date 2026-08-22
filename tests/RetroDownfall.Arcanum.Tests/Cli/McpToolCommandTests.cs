using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class McpToolCommandTests
{

    [Fact]

    public void Help_lists_complete_mcp_and_tool_command_families()
    {

        CliTestResult mcp = RunCommand(new RecordingHandler(), ["mcp", "--help"]);

        CliTestResult tool = RunCommand(new RecordingHandler(), ["tool", "--help"]);

        Assert.Equal(0, mcp.ExitCode);

        Assert.Equal(0, tool.ExitCode);

        Assert.Contains("list", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("show", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("start", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("stop", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("restart", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("reload", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("trust", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("tools", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("invoke", mcp.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("list", tool.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("show", tool.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("invoke", tool.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Mcp_list_shows_safe_scope_transport_trust_lifecycle_tool_count_and_error()
    {

        McpServerInfo server = WorkspaceServer() with
        {

            ErrorMessage = "connection refused",

            Command = "secret-command",

            Arguments = ["--token", "secret-value"],

            Url = "https://secret.example.test/mcp",

        };

        RecordingHandler handler = new(_ => McpListResponse([server]));

        CliTestResult result = RunCommand(handler, ["mcp", "list"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Scope", result.Output, StringComparison.Ordinal);

        Assert.Contains("Transport", result.Output, StringComparison.Ordinal);

        Assert.Contains("Trust", result.Output, StringComparison.Ordinal);

        Assert.Contains("Lifecycle", result.Output, StringComparison.Ordinal);

        Assert.Contains("Tools", result.Output, StringComparison.Ordinal);

        Assert.Contains("Last", result.Output, StringComparison.Ordinal);

        Assert.Contains("error", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("workspace", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("trusted", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("connection", result.Output, StringComparison.Ordinal);

        Assert.Contains("refused", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("secret-command", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("secret-value", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("secret.example", result.Output, StringComparison.Ordinal);

    }

    [Theory]

    [InlineData("start")]

    [InlineData("stop")]

    [InlineData("restart")]

    public void Mcp_lifecycle_commands_resolve_scope_then_call_the_server_api(string action)
    {

        RecordingHandler handler = new(request =>
        {

            if (request.Method == HttpMethod.Get)
            {

                return McpListResponse([WorkspaceServer()]);

            }

            return BooleanResponse();

        });

        CliTestResult result = RunCommand(handler, ["mcp", action, "workspace-server"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(3, handler.Requests.Count);

        HttpRequestMessage request = handler.Requests[^1];

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal($"/api/mcp/workspace-server/{action}", request.RequestUri!.AbsolutePath);

        Assert.Contains(
            "workingDirectory=%2Fsrv%2Fworkspace",
            request.RequestUri.Query,
            StringComparison.Ordinal);

    }

    [Fact]

    public void Mcp_show_reads_the_scope_disambiguated_server_detail()
    {

        RecordingHandler handler = new(request =>
            request.RequestUri!.AbsolutePath == "/api/mcp"
                ? McpListResponse([WorkspaceServer()])
                : CreateResponse(
                    new ApiResponse<McpServerInfo>(WorkspaceServer(), true, null),
                    ArcanumJsonContext.Default.ApiResponseMcpServerInfo));

        CliTestResult result = RunCommand(handler, ["mcp", "show", "workspace-server"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(3, handler.Requests.Count);

        Assert.Equal("/api/mcp/workspace-server", handler.Requests[^1].RequestUri!.AbsolutePath);

        Assert.Contains("trusted", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("workspace", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Mcp_reload_and_trust_send_explicit_workspace_scope()
    {

        RecordingHandler handler = new(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/reload", StringComparison.Ordinal)
                ? CreateResponse(
                    new ApiResponse<string>("reloaded", true, null),
                    ArcanumJsonContext.Default.ApiResponseString)
                : BooleanResponse());

        CliTestResult reload = RunCommand(
            handler,
            ["mcp", "reload", "--workspace", "/srv/workspace"]);

        CliTestResult trust = RunCommand(
            handler,
            ["mcp", "trust", "/srv/workspace"]);

        Assert.Equal(0, reload.ExitCode);

        Assert.Equal(0, trust.ExitCode);

        Assert.Equal("/api/mcp/reload", handler.Requests[0].RequestUri!.AbsolutePath);

        Assert.Equal("/api/mcp/trust-workspace", handler.Requests[1].RequestUri!.AbsolutePath);

        Assert.All(
            handler.Requests,
            request => Assert.Contains(
                "\"workingDirectory\":\"/srv/workspace\"",
                ReadBody(request),
                StringComparison.Ordinal));

    }

    [Fact]

    public void Mcp_tools_lists_only_the_selected_servers_tools()
    {

        RecordingHandler handler = new(_ => McpListResponse([WorkspaceServer()]));

        CliTestResult result = RunCommand(handler, ["mcp", "tools", "workspace-server"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("external_search", result.Output, StringComparison.Ordinal);

        Assert.Contains("external_read", result.Output, StringComparison.Ordinal);

        Assert.Contains("workspace-server", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Mcp_tools_preserves_server_owned_windows_workspace_paths()
    {

        const string serverPath = @"C:\srv\workspace";

        McpServerInfo server = WorkspaceServer() with
        {

            WorkingDirectory = serverPath,

        };

        RecordingHandler handler = new(_ => McpListResponse([server]));

        CliTestResult result = RunCommand(
            handler,
            ["mcp", "tools", "workspace-server", "--workspace", serverPath]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("external_search", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Mcp_invoke_posts_inline_json_to_external_diagnostic_route()
    {

        RecordingHandler handler = new(request =>
        {

            if (request.RequestUri!.AbsolutePath == "/api/intelligence/arsenal")
            {

                return ArsenalResponse();

            }

            McpToolInvokeResponse response = new()
            {

                Result = JsonSerializer.SerializeToElement(new { answer = 42 }),

                ServerName = "workspace-server",

                ToolName = "external_search",

                DurationMs = 12,

                Truncated = false,

            };

            return CreateResponse(
                new ApiResponse<McpToolInvokeResponse>(response, true, null),
                ArcanumJsonContext.Default.ApiResponseMcpToolInvokeResponse);

        });

        CliTestResult result = RunCommand(
            handler,
            [
                "mcp",
                "invoke",
                "external_search",
                "{\"query\":\"dragons\"}",
                "--server",
                "workspace-server",
                "--workspace",
                "/srv/workspace",
            ]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        HttpRequestMessage request = handler.Requests[1];

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/mcp/tools/invoke", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"toolName\":\"external_search\"", body, StringComparison.Ordinal);

        Assert.Contains("\"query\":\"dragons\"", body, StringComparison.Ordinal);

        Assert.Contains("\"serverName\":\"workspace-server\"", body, StringComparison.Ordinal);

        Assert.Contains("\"workingDirectory\":\"/srv/workspace\"", body, StringComparison.Ordinal);

        Assert.Contains("42", result.Output, StringComparison.Ordinal);

        Assert.Contains("12", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Mcp_invoke_explains_forbidden_art_policy_without_invoking_it()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<McpToolInvokeResponse>(
                null,
                false,
                new Error(
                    "Mcp.DiagnosticBlocked",
                    "This tool cannot be invoked from the diagnostic endpoint because it is a Forbidden Art or requires the Master tool execution pipeline.")),
            ArcanumJsonContext.Default.ApiResponseMcpToolInvokeResponse,
            HttpStatusCode.BadRequest));

        CliTestResult result = RunCommand(
            handler,
            ["mcp", "invoke", "execute_command", "{}"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("Forbidden Art", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Master", result.Error, StringComparison.OrdinalIgnoreCase);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/mcp/tools/invoke", request.RequestUri!.AbsolutePath);

    }

    [Fact]

    public void Mcp_invoke_explains_that_the_internal_server_is_not_a_diagnostic_target()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            [
                "mcp",
                "invoke",
                "local_time",
                "{}",
                "--server",
                "arcanum-internal",
            ]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("internal", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("external", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("tool invoke", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(handler.Requests);

    }

    [Fact]

    public void Tool_list_and_show_use_workspace_scoped_arsenal()
    {

        RecordingHandler handler = new(_ => ArsenalResponse());

        CliTestResult list = RunCommand(
            handler,
            ["tool", "list", "--workspace", "/srv/workspace"]);

        CliTestResult show = RunCommand(
            handler,
            ["tool", "show", "local_time", "--workspace", "/srv/workspace"]);

        Assert.Equal(0, list.ExitCode);

        Assert.Equal(0, show.ExitCode);

        Assert.Contains("local_time", list.Output, StringComparison.Ordinal);

        Assert.Contains("system_info", list.Output, StringComparison.Ordinal);

        Assert.Contains("built-in", show.Output, StringComparison.OrdinalIgnoreCase);

        Assert.All(
            handler.Requests,
            request => Assert.Contains(
                "\"workingDirectory\":\"/srv/workspace\"",
                ReadBody(request),
                StringComparison.Ordinal));

    }

    [Fact]

    public void Tool_invoke_reads_json_from_at_file_and_posts_to_builtin_route()
    {

        string path = Path.GetTempFileName();

        try
        {

            File.WriteAllText(path, "{\"timezone\":\"UTC\"}");

            RecordingHandler handler = new(request =>
            {

                if (request.RequestUri!.AbsolutePath == "/api/intelligence/arsenal")
                {

                    return ArsenalResponse();

                }

                ToolInvokeResponse response = new()
                {

                    Result = JsonSerializer.SerializeToElement("12:00"),

                };

                return CreateResponse(
                    new ApiResponse<ToolInvokeResponse>(response, true, null),
                    ArcanumJsonContext.Default.ApiResponseToolInvokeResponse);

            });

            CliTestResult result = RunCommand(
                handler,
                ["tool", "invoke", "local_time", $"@{path}"]);

            Assert.True(
                result.ExitCode == 0,
                result.Output + global::System.Environment.NewLine + result.Error);

            HttpRequestMessage request = handler.Requests[1];

            Assert.Equal("/api/tools/invoke", request.RequestUri!.AbsolutePath);

            Assert.Contains("\"timezone\":\"UTC\"", ReadBody(request), StringComparison.Ordinal);

            Assert.Contains("12:00", result.Output, StringComparison.Ordinal);

        }
        finally
        {

            File.Delete(path);

        }

    }

    [Fact]

    public void Tool_invoke_rejects_invalid_json_before_any_api_call()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            ["tool", "invoke", "local_time", "not-json"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("JSON object", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(handler.Requests);

    }

    [Fact]

    public async Task Tool_invoke_reads_json_from_redirected_stdin()
    {

        RecordingHandler handler = new(request =>
        {

            if (request.RequestUri!.AbsolutePath == "/api/intelligence/arsenal")
            {

                return ArsenalResponse();

            }

            ToolInvokeResponse response = new()
            {

                Result = JsonSerializer.SerializeToElement("ok"),

            };

            return CreateResponse(
                new ApiResponse<ToolInvokeResponse>(response, true, null),
                ArcanumJsonContext.Default.ApiResponseToolInvokeResponse);

        });

        CliTestResult result = await RunCommandAsync(
            handler,
            ["tool", "invoke", "local_time"],
            "{\"timezone\":\"America/New_York\"}");

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(
            "\"timezone\":\"America/New_York\"",
            ReadBody(handler.Requests[1]),
            StringComparison.Ordinal);

    }

    [Fact]

    public void Tool_arguments_reject_inline_input_over_the_owned_cap()
    {

        bool success = ToolArgumentReader.TryRead(
            "{\"value\":\"" + new string('x', ToolArgumentReader.MaxArgumentBytes) + "\"}",
            out _,
            out string? error);

        Assert.False(success);

        Assert.Contains("input limit", error, StringComparison.OrdinalIgnoreCase);

    }

    private static McpServerInfo WorkspaceServer() =>
        new(
            "workspace-server",
            "/srv/workspace",
            McpServerTransport.Stdio,
            false,
            null,
            [],
            null,
            McpServerState.Running,
            null,
            ["external_search", "external_read"],
            DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

    private static WorkspaceArsenalDto Arsenal() =>
        new(
            [],
            ["local_time", "system_info"],
            [
                new McpServerStatusDto(
                    "workspace-server",
                    "running",
                    2,
                    ["external_search", "external_read"],
                    null),
            ],
            []);

    private static HttpResponseMessage McpListResponse(McpServerInfo[] servers) =>
        CreateResponse(
            new ApiResponse<McpServerInfo[]>(servers, true, null),
            ArcanumJsonContext.Default.ApiResponseMcpServerInfoArray);

    private static HttpResponseMessage ArsenalResponse() =>
        CreateResponse(
            new ApiResponse<WorkspaceArsenalDto>(Arsenal(), true, null),
            ArcanumJsonContext.Default.ApiResponseWorkspaceArsenalDto);

    private static HttpResponseMessage BooleanResponse() =>
        CreateResponse(
            new ApiResponse<bool>(true, true, null),
            ArcanumJsonContext.Default.ApiResponseBoolean);

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

    private static string ReadBody(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()
        ?? string.Empty;

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

    private static async Task<CliTestResult> RunCommandAsync(
        RecordingHandler handler,
        string[] args,
        string input)
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

        return await CliTestHarness.RunAsync(
            services,
            args,
            input).ConfigureAwait(false);

    }

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
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {

                byte[] body = request.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();

                snapshot.Content = new ByteArrayContent(body);

            }

            Requests.Add(snapshot);

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);

        }

    }

}
