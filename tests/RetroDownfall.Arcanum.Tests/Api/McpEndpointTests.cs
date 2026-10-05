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

    /// <summary>
    /// R-336: <c>POST /api/mcp/trust-workspace</c> carries the digest of the preview the operator approved
    /// to the manager, and a failure keeps its typed code and the status the error mapper gives it.
    /// </summary>
    [SkippableTheory]
    [InlineData("""{"workingDirectory":"/srv/workspace","expectedConfigDigest":"ABC123"}""", "ABC123")]
    [InlineData("""{"workingDirectory":"/srv/workspace"}""", null)]
    [InlineData("""{"workingDirectory":"/srv/workspace","expectedConfigDigest":"  "}""", null)]
    public async Task PostTrustWorkspace_hands_the_expected_digest_to_the_manager(
        string requestJson,
        string? expectedDigest)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        StubMcpConnectionManager manager = new(
            new Error("Mcp.ConfigChanged", "The workspace mcp.json changed after it was previewed."));

        await using ArcanumWebApplicationFactory factory = CreateFactory(manager);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/mcp/trust-workspace",
            new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<bool> body = await ReadBooleanEnvelopeAsync(response);

        Assert.False(body.IsSuccess);

        Assert.Equal("Mcp.ConfigChanged", body.Error?.Code);

        Assert.Equal("/srv/workspace", manager.LastTrustedWorkspace);

        Assert.Equal(expectedDigest, manager.LastExpectedConfigDigest);
    }

    /// <summary>
    /// R-336: <c>POST /api/mcp/trust-workspace/preview</c> answers with the host's reading of the file and
    /// its digest in the ordinary envelope.
    /// </summary>
    [SkippableFact]
    public async Task PostTrustWorkspacePreview_returns_the_hosts_reading_and_digest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        McpWorkspaceTrustPreview preview = new(
            "/srv/workspace",
            ["The mcp.json in /srv/workspace defines 1 MCP server(s):", "  files [stdio] npx -y server-files"],
            Truncated: false,
            "DIGESTDIGEST");

        StubTrustPreviewer previewer = new(Result<McpWorkspaceTrustPreview>.Success(preview));

        await using ArcanumWebApplicationFactory factory = CreateFactory(
            new StubMcpConnectionManager(new Error("unused", "unused")),
            previewer);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/mcp/trust-workspace/preview",
            new StringContent("""{"workingDirectory":"/srv/workspace"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<McpWorkspaceTrustPreview>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseMcpWorkspaceTrustPreview);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.Equal(preview.Workspace, body.Data!.Workspace);

        Assert.Equal(preview.Lines, body.Data.Lines);

        Assert.Equal("DIGESTDIGEST", body.Data.ConfigDigest);

        Assert.False(body.Data.Truncated);

        Assert.Equal("/srv/workspace", previewer.LastWorkspace);
    }

    [SkippableTheory]
    [InlineData("Mcp.MissingConfig", HttpStatusCode.BadRequest)]
    [InlineData("Mcp.InvalidConfig", HttpStatusCode.BadRequest)]
    public async Task PostTrustWorkspacePreview_failure_keeps_its_typed_code(
        string code,
        HttpStatusCode expectedStatus)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        StubTrustPreviewer previewer = new(Result<McpWorkspaceTrustPreview>.Failure(new Error(code, "Described.")));

        await using ArcanumWebApplicationFactory factory = CreateFactory(
            new StubMcpConnectionManager(new Error("unused", "unused")),
            previewer);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/mcp/trust-workspace/preview",
            new StringContent("""{"workingDirectory":"/srv/workspace"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(expectedStatus, response.StatusCode);

        ApiResponse<McpWorkspaceTrustPreview>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseMcpWorkspaceTrustPreview);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(code, body.Error?.Code);
    }

    [SkippableFact]
    public async Task PostTrustWorkspacePreview_without_a_workspace_is_a_missing_workspace_failure()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        StubTrustPreviewer previewer = new(Result<McpWorkspaceTrustPreview>.Failure(new Error("unused", "unused")));

        await using ArcanumWebApplicationFactory factory = CreateFactory(
            new StubMcpConnectionManager(new Error("unused", "unused")),
            previewer);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/mcp/trust-workspace/preview",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<McpWorkspaceTrustPreview>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseMcpWorkspaceTrustPreview);

        Assert.NotNull(body);

        Assert.Equal(ErrorCodes.Mcp.MissingWorkspace, body.Error?.Code);

        Assert.Null(previewer.LastWorkspace);
    }

    private static ArcanumWebApplicationFactory CreateFactory(
        StubMcpConnectionManager manager,
        IMcpWorkspaceTrustPreviewer? previewer = null) =>
        new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<IMcpConnectionManager>();

                services.AddSingleton<IMcpConnectionManager>(manager);

                if (previewer is not null)
                {
                    services.RemoveAll<IMcpWorkspaceTrustPreviewer>();

                    services.AddSingleton(previewer);
                }
            },
        };

    private sealed class StubTrustPreviewer(Result<McpWorkspaceTrustPreview> answer) : IMcpWorkspaceTrustPreviewer
    {
        public string? LastWorkspace { get; private set; }

        public Task<Result<McpWorkspaceTrustPreview>> PreviewAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            LastWorkspace = workingDirectory;

            return Task.FromResult(answer);
        }
    }

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
        public string? LastTrustedWorkspace { get; private set; }

        public string? LastExpectedConfigDigest { get; private set; }

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

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, string? expectedConfigDigest = null, CancellationToken cancellationToken = default)
        {
            LastTrustedWorkspace = workingDirectory;

            LastExpectedConfigDigest = expectedConfigDigest;

            return Task.FromResult(Result.Failure(failure));
        }
    }
}
