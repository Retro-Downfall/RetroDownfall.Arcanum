using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliContextCrossGenerationTests
{

    private static readonly Guid CampaignId =
        Guid.Parse("51515151-5151-5151-5151-515151515151");

    private static readonly Guid SessionId =
        Guid.Parse("52525252-5252-5252-5252-525252525252");

    [Theory]
    [InlineData((byte)CliContextScope.Campaign)]
    [InlineData((byte)CliContextScope.Workspace)]
    [InlineData((byte)CliContextScope.Model)]
    [InlineData((byte)CliContextScope.Session)]
    public async Task Selection_does_not_persist_a_resource_that_disappears_before_client_admission(
        byte scopeValue)
    {

        CliContextScope scope = (CliContextScope)scopeValue;

        CliContextDocument retained = CliContextDocument.Empty with
        {
            CampaignId = Guid.Parse("53535353-5353-5353-5353-535353535353"),
            CampaignName = "retained-campaign",
            WorkspaceId = "retained-workspace",
            WorkspacePath = "/workspaces/retained",
            Model = "retained-model",
            SessionId = Guid.Parse("54545454-5454-5454-5454-545454545454"),
        };

        FakeContextStore store = new(retained);

        MutableHostHandler host = new() { Available = true };

        RecordingArcanumClientMutationBoundary boundary = new()
        {
            BeforeMutation = () => host.Available = false,
        };

        CliContextService service = CreateService(store, host, boundary);

        CliContextMutationResult result = await service.SelectAsync(
            scope,
            Identifier(scope),
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Equal(retained, store.Load());

        Assert.Equal(0, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

    }

    [Theory]
    [InlineData((byte)CliContextScope.Campaign)]
    [InlineData((byte)CliContextScope.Workspace)]
    public async Task Selection_persists_the_refreshed_host_payload_observed_inside_client_admission(
        byte scopeValue)
    {

        CliContextScope scope = (CliContextScope)scopeValue;

        FakeContextStore store = new(CliContextDocument.Empty);

        MutableHostHandler host = new()
        {
            Available = true,
            CampaignName = "replacement-campaign",
            WorkspacePath = "/replacement/workspace",
        };

        RecordingArcanumClientMutationBoundary boundary = new();

        CliContextService service = CreateService(store, host, boundary);

        CliContextMutationResult result = await service.SelectAsync(
            scope,
            Identifier(scope),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        if (scope is CliContextScope.Campaign)
        {

            Assert.Equal("replacement-campaign", store.Load().CampaignName);

        }
        else
        {

            Assert.Equal("/replacement/workspace", store.Load().WorkspacePath);

        }

        Assert.Equal(1, store.ExclusiveSaves);

    }

    [Fact]
    public async Task Stale_cleanup_revalidates_every_candidate_after_client_admission()
    {

        CliContextDocument retained = new(
            CliContextDocument.CurrentVersion,
            CampaignId,
            "selected-campaign",
            "selected-workspace",
            "/selected/workspace",
            "selected-model",
            SessionId);

        FakeContextStore store = new(retained);

        MutableHostHandler host = new() { Available = false };

        RecordingArcanumClientMutationBoundary boundary = new()
        {
            BeforeMutation = () => host.Available = true,
        };

        CliContextService service = CreateService(store, host, boundary);

        CliContextValidation validation = await service.ValidateAsync(
            noContext: false,
            CancellationToken.None);

        Assert.Equal(retained, store.Load());

        Assert.Equal(retained, validation.Active);

        Assert.Equal(0, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

    }

    private static string Identifier(CliContextScope scope) =>
        scope switch
        {
            CliContextScope.Campaign => CampaignId.ToString("D"),
            CliContextScope.Workspace => "selected-workspace",
            CliContextScope.Model => "selected-model",
            CliContextScope.Session => SessionId.ToString("D"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    private static CliContextService CreateService(
        FakeContextStore store,
        MutableHostHandler host,
        RecordingArcanumClientMutationBoundary boundary) =>
        new(
            store,
            store,
            new SelectedResourceCatalog(),
            new ArcanumApiClient(
                new FakeHttpClientFactory(host),
                new FakeSecretStore()),
            Options.Create(new ArcanumSettings()),
            boundary);

    private sealed class FakeContextStore(
        CliContextDocument document) :
        ICliContextStore,
        ICliContextExclusiveWriter
    {

        private CliContextDocument _document = document;

        internal int ExclusiveSaves { get; private set; }

        public string FilePath => "/tmp/arcanum-context-generation.json";

        public CliContextDocument Load() => _document;

        public void SaveUnderExclusive(CliContextDocument value)
        {

            ExclusiveSaves++;

            _document = value;

        }

    }

    private sealed class SelectedResourceCatalog : ICliResourceCatalog
    {

        public Task<ResourceSelectionResult<CampaignDto>> SelectCampaignAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<CampaignDto>.Selected(
                    Campaign("selected-campaign")));

        public Task<ResourceSelectionResult<WorkspaceInfo>> SelectWorkspaceAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<WorkspaceInfo>.Selected(
                    Workspace("/selected/workspace")));

        public Task<ResourceSelectionResult<ModelInfoDto>> SelectModelAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<ModelInfoDto>.Selected(Model()));

        public Task<ResourceSelectionResult<SessionSummaryDto>> SelectSessionAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<SessionSummaryDto>.Selected(
                    new SessionSummaryDto(
                        SessionId,
                        CampaignId,
                        "selected-session",
                        "Active",
                        0,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch)));

        public Task<ResourceSelectionResult<EntryDto>> SelectSessionEntryAsync(
            Guid sessionId,
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<PromptSummaryDto>> SelectPromptAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<SpellSummary>> SelectSpellAsync(
            string? identifier,
            string? workspace,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<ApprenticeSummaryDto>> SelectApprenticeAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<ProviderInfoDto>> SelectProviderAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<McpServerInfo>> SelectMcpServerAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        private static InvalidOperationException Unused() =>
            new("This cross-generation test did not select that resource kind.");

    }

    private sealed class MutableHostHandler : HttpMessageHandler
    {

        internal bool Available { get; set; }

        internal string CampaignName { get; set; } = "selected-campaign";

        internal string WorkspacePath { get; set; } = "/selected/workspace";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string path = request.RequestUri!.AbsolutePath;

            if (path == "/api/campaigns")
            {

                ListPageResult<CampaignDto> page = new(
                    Available ? [Campaign(CampaignName)] : [],
                    false,
                    null);

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<ListPageResult<CampaignDto>>(page, true, null),
                            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto)));

            }

            if (path == "/api/workspaces")
            {

                WorkspaceInfo[] workspaces = Available
                    ? [Workspace(WorkspacePath)]
                    : [];

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<WorkspaceInfo[]>(workspaces, true, null),
                            ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray)));

            }

            if (path == "/api/models")
            {

                ModelInfoDto[] models = Available ? [Model()] : [];

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<ModelInfoDto[]>(models, true, null),
                            ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray)));

            }

            if (path == $"/api/sessions/{SessionId:D}")
            {

                Result<SessionDetailDto> result = Available
                    ? Result<SessionDetailDto>.Success(Session())
                    : Result<SessionDetailDto>.Failure(
                        new Error(
                            ErrorCodes.Session.NotFound,
                            "Session was not found."));

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            ApiResponse<SessionDetailDto>.FromResult(result),
                            ArcanumJsonContext.Default.ApiResponseSessionDetailDto),
                        Available ? HttpStatusCode.OK : HttpStatusCode.NotFound));

            }

            throw new InvalidOperationException($"Unexpected request to {path}.");

        }

        private static HttpResponseMessage Json(
            byte[] payload,
            HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new ByteArrayContent(payload),
            };

    }

    private sealed class FakeHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(
            string encryptionSecret) => Task.CompletedTask;

    }

    private static CampaignDto Campaign(string name) =>
        new(
            CampaignId,
            name,
            "/campaigns/selected",
            WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static WorkspaceInfo Workspace(string path) =>
        new(
            "selected-workspace",
            "selected-workspace",
            path,
            WorkspaceType.Custom,
            DateTimeOffset.UnixEpoch);

    private static ModelInfoDto Model() =>
        new(
            "selected-model",
            "selected-provider",
            "OpenAICompatible",
            "https://provider.invalid/v1",
            8_192);

    private static SessionDetailDto Session() =>
        new(
            SessionId,
            CampaignId,
            "selected-session",
            "Active",
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            0);

}
