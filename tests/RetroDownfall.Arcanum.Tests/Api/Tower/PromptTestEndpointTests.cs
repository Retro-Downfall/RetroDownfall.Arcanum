using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class PromptTestEndpointTests : IDisposable
{
    private const string Sentinel = "SENTINEL-prompt-test-codex-5d41c0a7";

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        $"arcanum-prompt-test-{Guid.NewGuid():N}");

    public PromptTestEndpointTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [SkippableFact]
    public async Task Test_with_codexPath_under_a_workingDirectory_outside_the_configured_roots_is_refused()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string directory = Path.Combine(_scratch, "outside");

        Directory.CreateDirectory(directory);

        string secret = Path.Combine(directory, "secret.txt");

        File.WriteAllText(secret, Sentinel);

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([]);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(directory, null, null, secret, null));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.Contains(ErrorCodes.Spell.PathNotAllowed, json);

        Assert.DoesNotContain(Sentinel, json);
    }

    [SkippableFact]
    public async Task Test_with_a_workingDirectory_outside_the_configured_roots_and_no_codexPath_succeeds_without_using_it_as_a_tool_workspace()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string directory = Path.Combine(_scratch, "outside-no-codex");

        Directory.CreateDirectory(directory);

        RecordingMcpConnectionManager mcp = new();

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([], mcp);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(directory, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(string.IsNullOrWhiteSpace(Assert.Single(mcp.ToolWorkspaces)));
    }

    [SkippableFact]
    public async Task Test_sent_the_way_arcanum_prompt_test_sends_it_succeeds_when_no_workspace_roots_are_configured()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingMcpConnectionManager mcp = new();

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([], mcp);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        // The shipping CLI always sends its own current directory and nothing else; on a stock install
        // (empty Arcanum:Security:SpellWorkspaceRoots) that directory is never allowlisted.
        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(System.Environment.CurrentDirectory, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(string.IsNullOrWhiteSpace(Assert.Single(mcp.ToolWorkspaces)));
    }

    [SkippableFact]
    public async Task Test_with_a_workingDirectory_under_a_configured_root_and_no_codexPath_lists_tools_for_that_workspace()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string directory = Path.Combine(_scratch, "allowed-no-codex");

        Directory.CreateDirectory(directory);

        RecordingMcpConnectionManager mcp = new();

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([_scratch], mcp);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(directory, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(Path.GetFullPath(directory), Assert.Single(mcp.ToolWorkspaces));
    }

    [SkippableFact]
    public async Task Test_with_a_workingDirectory_that_does_not_exist_is_still_refused_as_an_invalid_workspace()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([_scratch]);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(Path.Combine(_scratch, "missing"), null, null, null, null));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Contains(ErrorCodes.Spell.InvalidWorkspace, json);
    }

    [SkippableFact]
    public async Task Test_without_a_workingDirectory_still_succeeds_when_no_workspace_roots_are_configured()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([]);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(null, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Test_with_codexPath_under_a_configured_root_still_reads_the_file()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string directory = Path.Combine(_scratch, "allowed");

        Directory.CreateDirectory(directory);

        string codex = Path.Combine(directory, "codex.md");

        File.WriteAllText(codex, Sentinel);

        await using ArcanumWebApplicationFactory factory = CreateFactoryWithSpellRoots([_scratch]);

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid promptId = await CreateGlobalPromptAsync(client);

        HttpResponseMessage response = await PostTestAsync(
            client,
            promptId,
            new TestPromptRequest(directory, null, null, codex, null));

        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(Sentinel, json);
    }

    private static ArcanumWebApplicationFactory CreateFactoryWithSpellRoots(
        string[] spellRoots,
        IMcpConnectionManager? mcp = null) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Security = settings.Security with
                {
                    SpellWorkspaceRoots = spellRoots,
                },
            },
            ServiceOverrides = services =>
            {
                if (mcp is not null)
                {
                    _ = services.AddSingleton(mcp);
                }
            },
        };

    private static async Task<Guid> CreateGlobalPromptAsync(HttpClient client)
    {
        CreatePromptRequest request = new(
            $"prompt-test-{Guid.NewGuid():N}",
            "1.0.0",
            "Hello",
            "Test prompt",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreatePromptRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/prompts",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<PromptDetailDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponsePromptDetailDto);

        return body!.Data!.Id;
    }

    private static async Task<HttpResponseMessage> PostTestAsync(HttpClient client, Guid promptId, TestPromptRequest request)
    {
        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.TestPromptRequest);

        return await client.PostAsync(
            $"/api/prompts/{promptId}/test",
            new StringContent(payload, Encoding.UTF8, "application/json"));
    }

    private sealed class RecordingMcpConnectionManager : IMcpConnectionManager
    {
        private readonly List<string?> _toolWorkspaces = [];

        public IReadOnlyList<string?> ToolWorkspaces
        {
            get
            {
                lock (_toolWorkspaces)
                {
                    return [.. _toolWorkspaces];
                }
            }
        }

        public Task InitializeAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        public Task<McpServerInfo[]> GetAllStatusesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(
            string? workingDirectory,
            CancellationToken cancellationToken = default)
        {
            lock (_toolWorkspaces)
            {
                _toolWorkspaces.Add(workingDirectory);
            }

            return Task.FromResult<IReadOnlyList<AITool>>([]);
        }

        public Task<AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AIFunction?>(null);

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpServerStatusDto>());

        public Task ReloadAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(
            string workingDirectory,
            string? expectedConfigDigest = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
}
