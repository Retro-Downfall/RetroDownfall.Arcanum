using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class McpEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public McpEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetMcp_WithValidApiKey_ReturnsServerStatusEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<McpServerInfo[]>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseMcpServerInfoArray);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task PostStart_UnknownServer_ReturnsNotFoundWithServerNotFoundCode()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/mcp/does-not-exist/start", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiResponse<bool> body = await ReadBooleanEnvelopeAsync(response);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Mcp.ServerNotFound, body.Error?.Code);

    }

    [SkippableTheory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task PostLifecycle_WorkspaceNotTrusted_ReturnsForbidden(string verb)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        StubMcpConnectionManager manager = new(
            new Error(ErrorCodes.Mcp.WorkspaceNotTrusted, "Workspace-local MCP servers require operator approval."));

        await using ArcanumWebApplicationFactory factory = CreateFactory(manager);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync($"/api/mcp/local/{verb}", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        ApiResponse<bool> body = await ReadBooleanEnvelopeAsync(response);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Mcp.WorkspaceNotTrusted, body.Error?.Code);

    }

    [SkippableFact]
    public async Task PostStart_UnmappedFailureCode_StillReturnsBadRequest()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        StubMcpConnectionManager manager = new(new Error("Mcp.SseNotSupported", "SSE transport is not yet supported."));

        await using ArcanumWebApplicationFactory factory = CreateFactory(manager);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/mcp/sse-server/start", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<bool> body = await ReadBooleanEnvelopeAsync(response);

        Assert.False(body.IsSuccess);

        Assert.Equal("Mcp.SseNotSupported", body.Error?.Code);

    }

    private static ArcanumWebApplicationFactory CreateFactory(StubMcpConnectionManager manager) =>
        new()
        {
            ServiceOverrides = services =>
            {

                services.RemoveAll<IMcpConnectionManager>();

                services.AddSingleton<IMcpConnectionManager>(manager);

            },
        };

    private static async Task<ApiResponse<bool>> ReadBooleanEnvelopeAsync(HttpResponseMessage response)
    {

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<bool>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        return body;

    }

    /// <summary>
    /// Fails every lifecycle transition with a fixed error so the endpoint's status resolution is the
    /// only thing under test.
    /// </summary>
    private sealed class StubMcpConnectionManager(Error failure) : IMcpConnectionManager
    {

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure(failure));

        public Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure(failure));

        public Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure(failure));

        public Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AITool>>([]);

        public Task<AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AIFunction?>(null);

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpServerStatusDto>());

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure(failure));

    }

}
