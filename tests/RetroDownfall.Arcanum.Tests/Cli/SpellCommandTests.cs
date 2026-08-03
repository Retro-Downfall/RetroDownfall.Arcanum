using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class SpellCommandTests
{

    [Fact]
    public void Spell_list_calls_get_spells_with_workspace_query()
    {

        SpellSummary summary = new("greet", "Say hello", SpellSource.Workspace, ["demo"]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellSummary[]>([summary], true, null),
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray));

        CliTestResult result = RunCommand(handler, ["spell", "list", "--workspace", "/tmp/ws"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/spells", request.RequestUri!.AbsolutePath);

        Assert.Contains("workspace=", request.RequestUri!.Query, StringComparison.Ordinal);

    }

    [Fact]
    public void Spell_get_binds_name_argument()
    {

        SpellDetail detail = new(
            "greet",
            "Say hello",
            SpellSource.Workspace,
            [],
            null,
            null,
            "Hello!",
            null,
            null,
            [],
            [],
            "/tmp/ws",
            "/tmp/ws/spells/greet/SPELL.md");

        SpellSummary summary = new("greet", "Say hello", SpellSource.Workspace, []);
        RecordingHandler handler = new(request =>
            request.RequestUri!.AbsolutePath == "/api/spells"
                ? CreateResponse(
                    new ApiResponse<SpellCatalogPage>(
                        new SpellCatalogPage([summary], false, null, null),
                        true,
                        null),
                    ArcanumJsonContext.Default.ApiResponseSpellCatalogPage)
                : CreateResponse(
                    new ApiResponse<SpellDetail>(detail, true, null),
                    ArcanumJsonContext.Default.ApiResponseSpellDetail));

        CliTestResult result = RunCommand(handler, ["spell", "get", "greet"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        Assert.Contains(
            "paged=true",
            handler.Requests[0].RequestUri!.Query,
            StringComparison.Ordinal);

        HttpRequestMessage request = handler.Requests[1];

        Assert.Equal("/api/spells/greet", request.RequestUri!.AbsolutePath);

    }

    [Fact]

    public void Spell_get_end_of_options_preserves_name_equal_to_private_launcher_flag()
    {

        const string SpellName = "--arcanum-deep-link";

        SpellDetail detail = Detail(SpellName, "/tmp/ws");

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellDetail>(detail, true, null),
            ArcanumJsonContext.Default.ApiResponseSpellDetail));

        FakeResourceCatalog resources = new()
        {

            SpellResult = ResourceSelectionResult<SpellSummary>.Selected(
                new SpellSummary(
                    SpellName,
                    "A valid hyphenated Spell name.",
                    SpellSource.Workspace,
                    [])),

        };

        CliTestResult result = RunCommand(
            handler,
            ["spell", "get", "--", SpellName],
            resources);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, resources.SpellSelectionCount);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.EndsWith(
            "/api/spells/--arcanum-deep-link",
            request.RequestUri!.AbsolutePath,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Command Center application link",
            result.Output + result.Error,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Spell_get_resolves_workspace_id_to_server_path_before_spell_calls()
    {

        const string WorkspaceId = "workspace-opaque-42";

        const string WorkspacePath = "/server/workspaces/Spell Lab";

        SpellDetail detail = Detail("greet", WorkspacePath);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellDetail>(detail, true, null),
            ArcanumJsonContext.Default.ApiResponseSpellDetail));

        FakeResourceCatalog resources = new()
        {

            WorkspaceResult = ResourceSelectionResult<WorkspaceInfo>.Selected(
                Workspace(WorkspaceId, WorkspacePath)),

            SpellResult = ResourceSelectionResult<SpellSummary>.Selected(
                new SpellSummary(
                    "greet",
                    "Say hello",
                    SpellSource.Workspace,
                    [])),

        };

        CliTestResult result = RunCommand(
            handler,
            ["spell", "get", "greet", "--workspace", WorkspaceId],
            resources);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(WorkspaceId, resources.WorkspaceIdentifier);

        Assert.Equal(WorkspacePath, resources.SpellWorkspace);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/spells/greet", request.RequestUri!.AbsolutePath);

        Assert.Contains(
            $"workspace={Uri.EscapeDataString(WorkspacePath)}",
            request.RequestUri.Query,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            WorkspaceId,
            request.RequestUri.Query,
            StringComparison.Ordinal);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Spell_get_workspace_cancel_or_error_stops_before_spell_calls(
        bool cancelled)
    {

        const string WorkspaceId = "workspace-opaque-42";

        RecordingHandler handler = new();

        FakeResourceCatalog resources = new()
        {

            WorkspaceResult = cancelled
                ? ResourceSelectionResult<WorkspaceInfo>.Cancelled()
                : ResourceSelectionResult<WorkspaceInfo>.Failure(
                    "The workspace is unavailable."),

            SpellResult = ResourceSelectionResult<SpellSummary>.Selected(
                new SpellSummary(
                    "greet",
                    "Say hello",
                    SpellSource.Workspace,
                    [])),

        };

        CliTestResult result = RunCommand(
            handler,
            ["spell", "get", "greet", "--workspace", WorkspaceId],
            resources);

        Assert.Equal(cancelled ? 0 : 1, result.ExitCode);

        Assert.Equal(WorkspaceId, resources.WorkspaceIdentifier);

        Assert.Equal(0, resources.SpellSelectionCount);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Spell_delete_requires_workspace_without_calling_api()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["spell", "delete", "greet"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Spell_execute_posts_prompt_and_prints_response()
    {

        PromptResponseDto response = new("Hello, world!", null);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<PromptResponseDto>(response, true, null),
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto));

        CliTestResult result = RunCommand(handler, ["spell", "execute", "greet", "--input", "hi there"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/spells/greet/execute", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"prompt\":\"hi there\"", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Spell_create_merges_repeated_tag_flags_into_a_single_array()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<bool>(true, true, null),
            ArcanumJsonContext.Default.ApiResponseBoolean));

        CliTestResult result = RunCommand(
            handler,
            ["spell", "create", "--name", "greet", "--workspace", "/tmp/ws", "--tag", "a", "--tag", "b", "--tag", "c"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        string body = ReadBody(request);

        Assert.Contains("\"tags\":[\"a\",\"b\",\"c\"]", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Spell_search_binds_query_options()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellSummary[]>([], true, null),
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray));

        CliTestResult result = RunCommand(handler, ["spell", "search", "--query", "greet", "--tag", "demo"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/spells/search", request.RequestUri!.AbsolutePath);

        Assert.Contains("q=greet", request.RequestUri!.Query, StringComparison.Ordinal);

        Assert.Contains("tag=demo", request.RequestUri!.Query, StringComparison.Ordinal);

    }

    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args,
        ICliResourceCatalog? resourceCatalog = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        if (resourceCatalog is not null)
        {

            services.RemoveAll<ICliResourceCatalog>();

            services.AddSingleton(resourceCatalog);

        }

        return CliTestHarness.Run(services, args);

    }

    private static SpellDetail Detail(string name, string workspacePath) =>
        new(
            name,
            "Say hello",
            SpellSource.Workspace,
            [],
            null,
            null,
            "Hello!",
            null,
            null,
            [],
            [],
            workspacePath,
            $"{workspacePath}/spells/{name}/SPELL.md");

    private static WorkspaceInfo Workspace(string id, string path) =>
        new(
            id,
            "Spell Lab",
            path,
            WorkspaceType.Spell,
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
            Persisted: true);

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

    private static string ReadBody(HttpRequestMessage request)
    {

        if (request.Content is null)
        {
            return string.Empty;
        }

        return request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    }

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

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

    private sealed class FakeResourceCatalog : ICliResourceCatalog
    {

        public ResourceSelectionResult<WorkspaceInfo> WorkspaceResult { get; init; } =
            ResourceSelectionResult<WorkspaceInfo>.Failure(
                "Unexpected workspace selection.");

        public ResourceSelectionResult<SpellSummary> SpellResult { get; init; } =
            ResourceSelectionResult<SpellSummary>.Failure(
                "Unexpected spell selection.");

        public string? WorkspaceIdentifier { get; private set; }

        public string? SpellWorkspace { get; private set; }

        public int SpellSelectionCount { get; private set; }

        public Task<ResourceSelectionResult<WorkspaceInfo>> SelectWorkspaceAsync(
            string? identifier,
            CancellationToken cancellationToken)
        {

            WorkspaceIdentifier = identifier;

            return Task.FromResult(WorkspaceResult);

        }

        public Task<ResourceSelectionResult<SpellSummary>> SelectSpellAsync(
            string? identifier,
            string? workspace,
            CancellationToken cancellationToken)
        {

            SpellSelectionCount++;

            SpellWorkspace = workspace;

            return Task.FromResult(SpellResult);

        }

        public Task<ResourceSelectionResult<CampaignDto>> SelectCampaignAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Unexpected<CampaignDto>();

        public Task<ResourceSelectionResult<SessionSummaryDto>> SelectSessionAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Unexpected<SessionSummaryDto>();

        public Task<ResourceSelectionResult<EntryDto>> SelectSessionEntryAsync(
            Guid sessionId,
            string? identifier,
            CancellationToken cancellationToken) =>
            Unexpected<EntryDto>();

        public Task<ResourceSelectionResult<PromptSummaryDto>> SelectPromptAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Unexpected<PromptSummaryDto>();

        public Task<ResourceSelectionResult<ApprenticeSummaryDto>> SelectApprenticeAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Unexpected<ApprenticeSummaryDto>();

        public Task<ResourceSelectionResult<ModelInfoDto>> SelectModelAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Unexpected<ModelInfoDto>();

        public Task<ResourceSelectionResult<ProviderInfoDto>> SelectProviderAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Unexpected<ProviderInfoDto>();

        public Task<ResourceSelectionResult<McpServerInfo>> SelectMcpServerAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Unexpected<McpServerInfo>();

        private static Task<ResourceSelectionResult<T>> Unexpected<T>()
            where T : class =>
            Task.FromResult(
                ResourceSelectionResult<T>.Failure(
                    "Unexpected resource selection."));

    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {

            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {

                byte[] body = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();

                snapshot.Content = new ByteArrayContent(body);

                foreach (KeyValuePair<string, IEnumerable<string>> contentHeader in request.Content.Headers)
                {
                    snapshot.Content.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);
                }

            }

            Requests.Add(snapshot);

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);

        }

    }

}
