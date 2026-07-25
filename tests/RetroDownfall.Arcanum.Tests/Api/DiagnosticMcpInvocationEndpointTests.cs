using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Exercises the mapped HTTP handler through a fresh host per test. The fake is limited to the MCP
/// transport boundary; edition gating, request binding, service policy, error mapping, and response
/// serialization all run through production code.
/// </summary>
[Collection("ApiHost")]
public sealed class DiagnosticMcpInvocationEndpointTests : IDisposable
{

    private readonly string? _originalEdition =
        global::System.Environment.GetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar);

    public DiagnosticMcpInvocationEndpointTests()
    {

        global::System.Environment.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, null);

    }

    [SkippableFact]
    public async Task PostInvoke_NonDevelopmentEdition_ReturnsDiagnosticDisabled()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        EndpointMcpConnectionManager manager = new();
        await using ArcanumWebApplicationFactory factory = CreateFactory(ArcanumEdition.Local, manager);
        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostInvokeAsync(
            client,
            """{"toolName":"echo","arguments":{"value":7},"serverName":"external"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        ApiResponse<McpToolInvokeResponse> body = await ReadBodyAsync(response);
        Assert.False(body.IsSuccess);
        Assert.Null(body.Data);
        Assert.Equal("Mcp.DiagnosticDisabled", body.Error?.Code);
        Assert.Contains("Arcanum:Edition=Development", body.Error?.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
        Assert.Equal(0, manager.StatusQueryCount);
        Assert.Equal(0, manager.Tool.InvocationCount);

    }

    [SkippableFact]
    public async Task PostInvoke_DevelopmentEdition_ServiceFailureMapsToBadRequest()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        EndpointMcpConnectionManager manager = new();
        await using ArcanumWebApplicationFactory factory = CreateFactory(ArcanumEdition.Development, manager);
        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostInvokeAsync(
            client,
            """{"toolName":"   ","arguments":{}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ApiResponse<McpToolInvokeResponse> body = await ReadBodyAsync(response);
        Assert.False(body.IsSuccess);
        Assert.Null(body.Data);
        Assert.Equal(ErrorCodes.Validation.InvalidBody, body.Error?.Code);
        Assert.Equal("'toolName' is required.", body.Error?.Message);
        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
        Assert.Equal(0, manager.StatusQueryCount);
        Assert.Equal(0, manager.Tool.InvocationCount);

    }

    [SkippableFact]
    public async Task PostInvoke_DevelopmentEdition_ReturnsExternalToolOutcome()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        EndpointMcpConnectionManager manager = new();
        await using ArcanumWebApplicationFactory factory = CreateFactory(ArcanumEdition.Development, manager);
        using HttpClient client = factory.CreateAuthenticatedClient();
        string payload = $$"""
            {
              "toolName": "echo",
              "arguments": { "value": 7 },
              "serverName": "external",
              "workingDirectory": "{{factory.TempHome.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
            }
            """;

        HttpResponseMessage response = await PostInvokeAsync(client, payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ApiResponse<McpToolInvokeResponse> body = await ReadBodyAsync(response);
        Assert.True(body.IsSuccess);
        Assert.Null(body.Error);
        Assert.NotNull(body.Data);
        Assert.Equal("external", body.Data.ServerName);
        Assert.Equal("echo", body.Data.ToolName);
        Assert.True(body.Data.DurationMs >= 0);
        Assert.False(body.Data.Truncated);
        Assert.Equal(42, body.Data.Result.GetProperty("answer").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.TraceId));
        Assert.Equal(1, manager.StatusQueryCount);
        Assert.Equal(factory.TempHome, manager.LastStatusWorkspace);
        Assert.Equal(factory.TempHome, manager.LastToolWorkspace);
        Assert.Equal(1, manager.Tool.InvocationCount);

    }

    private static ArcanumWebApplicationFactory CreateFactory(
        ArcanumEdition edition,
        EndpointMcpConnectionManager manager) =>
        new()
        {
            SettingsOverride = settings => settings with { Edition = edition },
            ServiceOverrides = services =>
            {

                services.RemoveAll<IMcpConnectionManager>();
                services.AddSingleton<IMcpConnectionManager>(manager);

            },
        };

    private static Task<HttpResponseMessage> PostInvokeAsync(HttpClient client, string payload) =>
        client.PostAsync(
            "/api/mcp/tools/invoke",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    private static async Task<ApiResponse<McpToolInvokeResponse>> ReadBodyAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();
        ApiResponse<McpToolInvokeResponse>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseMcpToolInvokeResponse);
        Assert.NotNull(body);
        return body;

    }

    public void Dispose()
    {

        global::System.Environment.SetEnvironmentVariable(
            ArcanumEnvironment.EditionEnvVar,
            _originalEdition);

    }

    private sealed class EndpointMcpConnectionManager : IMcpConnectionManager
    {

        public EchoFunction Tool { get; } = new();

        public int StatusQueryCount { get; private set; }

        public string? LastStatusWorkspace { get; private set; }

        public string? LastToolWorkspace { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(
            string name,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> StopAsync(
            string name,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RestartAsync(
            string name,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<McpServerInfo?> GetStatusAsync(
            string name,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AITool>>([Tool]);

        public Task<AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {

            LastToolWorkspace = workingDirectory;
            AIFunction? result =
                string.Equals(serverName, "external", StringComparison.Ordinal)
                && string.Equals(toolName, Tool.Name, StringComparison.Ordinal)
                    ? Tool
                    : null;
            return Task.FromResult(result);

        }

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {

            StatusQueryCount++;
            LastStatusWorkspace = workingDirectory;
            return Task.FromResult(new List<McpServerStatusDto>
            {
                new("external", "running", 1, [Tool.Name], ErrorMessage: null),
            });

        }

        public Task ReloadAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

    }

    private sealed class EchoFunction : AIFunction
    {

        public override string Name => "echo";

        public override string Description => "Returns a deterministic JSON response.";

        public int InvocationCount { get; private set; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {

            InvocationCount++;
            return ValueTask.FromResult<object?>("""{"answer":42}""");

        }

    }

}
