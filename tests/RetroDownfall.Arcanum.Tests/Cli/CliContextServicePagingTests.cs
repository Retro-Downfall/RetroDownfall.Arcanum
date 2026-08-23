using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliContextServicePagingTests
{

    private static readonly Guid StaleCampaignId =
        Guid.Parse("31313131-3131-3131-3131-313131313131");

    private static readonly Guid StaleSessionId =
        Guid.Parse("32323232-3232-3232-3232-323232323232");

    /// <summary>
    /// A host that answers <c>hasMore: true</c> with a cursor that does not advance must not spin the
    /// campaign paging loop. <c>ArcanumApiClient.ListLoreAsync</c> already refuses a non-advancing
    /// offset; the context service has to make the same guarantee because every <c>context</c>,
    /// <c>use</c>, and interactive <c>run</c> resolution walks this loop.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_stops_when_the_campaign_cursor_does_not_advance()
    {

        StuckCursorHandler handler = new();

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            new FakeSecretStore());

        FakeContextStore store = new();

        CliContextService service = new(
            store,
            store,
            new UnusedResourceCatalog(),
            client,
            Options.Create(new ArcanumSettings()),
            new RecordingArcanumClientMutationBoundary());

        CliContextValidation validation = await service.ValidateAsync(
            noContext: false,
            CancellationToken.None);

        Assert.Equal(1, handler.CampaignRequests);

        Assert.Empty(validation.Campaigns);

    }

    [Theory]
    [InlineData((byte)ArcanumClientMutationDisposition.Blocked)]
    [InlineData((byte)ArcanumClientMutationDisposition.Unsafe)]
    public async Task ValidateAsync_refused_stale_cleanup_retains_saved_context_and_reports_the_refusal(
        byte dispositionValue)
    {

        CliContextDocument retained = StaleContext();

        FakeContextStore store = new(retained);

        RecordingArcanumClientMutationBoundary boundary = new(
            (ArcanumClientMutationDisposition)dispositionValue);

        CliContextService service = CreateService(
            store,
            new StaleContextHandler(),
            boundary);

        CliContextValidation validation = await service.ValidateAsync(
            noContext: false,
            CancellationToken.None);

        Assert.Equal(retained, store.Load());

        Assert.Equal(CliContextDocument.Empty, validation.Active);

        Assert.Equal(0, store.UnprotectedSaves);

        Assert.Equal(0, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.Equal(4, validation.Warnings.Length);

        Assert.All(
            validation.Warnings,
            warning => Assert.Contains(
                "could not be cleared",
                warning,
                StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task ValidateAsync_completed_stale_cleanup_uses_one_exclusive_write()
    {

        FakeContextStore store = new(StaleContext());

        RecordingArcanumClientMutationBoundary boundary = new();

        CliContextService service = CreateService(
            store,
            new StaleContextHandler(),
            boundary);

        CliContextValidation validation = await service.ValidateAsync(
            noContext: false,
            CancellationToken.None);

        Assert.Equal(CliContextDocument.Empty, store.Load());

        Assert.Equal(CliContextDocument.Empty, validation.Active);

        Assert.Equal(0, store.UnprotectedSaves);

        Assert.Equal(1, store.ExclusiveSaves);

        Assert.Equal(1, boundary.Calls);

        Assert.All(
            validation.Warnings,
            warning => Assert.Contains(
                "was cleared",
                warning,
                StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task ValidateAsync_no_context_does_not_read_or_mutate_saved_context()
    {

        FakeContextStore store = new()
        {

            ThrowOnLoad = true,

        };

        RecordingArcanumClientMutationBoundary boundary = new();

        CliContextService service = CreateService(
            store,
            new StaleContextHandler(),
            boundary);

        CliContextValidation validation = await service.ValidateAsync(
            noContext: true,
            CancellationToken.None);

        Assert.Equal(CliContextDocument.Empty, validation.Active);

        Assert.Equal(0, boundary.Calls);

        Assert.Equal(0, store.ExclusiveSaves);

    }

    private static CliContextService CreateService(
        FakeContextStore store,
        HttpMessageHandler handler,
        RecordingArcanumClientMutationBoundary boundary) =>
        new(
            store,
            store,
            new UnusedResourceCatalog(),
            new ArcanumApiClient(
                new FakeHttpClientFactory(handler),
                new FakeSecretStore()),
            Options.Create(new ArcanumSettings()),
            boundary);

    private static CliContextDocument StaleContext() =>
        new(
            CliContextDocument.CurrentVersion,
            StaleCampaignId,
            "stale-campaign",
            "stale-workspace",
            "/workspaces/stale",
            "stale-model",
            StaleSessionId);

    private sealed class StuckCursorHandler : HttpMessageHandler
    {

        public int CampaignRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string path = request.RequestUri!.AbsolutePath;

            if (path == "/api/campaigns")
            {

                CampaignRequests++;

                if (CampaignRequests > 5)
                {

                    throw new InvalidOperationException(
                        $"The campaign paging loop followed a non-advancing cursor {CampaignRequests} times.");

                }

                ListPageResult<CampaignDto> page = new(
                    [Campaign()],
                    HasMore: true,
                    NextOffset: 0);

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<ListPageResult<CampaignDto>>(page, true, null),
                            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto)));

            }

            if (path == "/api/workspaces")
            {

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<WorkspaceInfo[]>([], true, null),
                            ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray)));

            }

            if (path == "/api/models")
            {

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<ModelInfoDto[]>([], true, null),
                            ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray)));

            }

            throw new InvalidOperationException($"Unexpected request to {path}.");

        }

        private static HttpResponseMessage Json(byte[] payload) =>
            new(HttpStatusCode.OK)
            {

                Content = new ByteArrayContent(payload),

            };

        private static CampaignDto Campaign() =>
            new(
                Guid.NewGuid(),
                "campaign-alpha",
                "/campaigns/alpha",
                WorkspaceType.Campaign,
                null,
                CampaignSettings.CreateDefault(),
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);

    }

    private sealed class StaleContextHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string path = request.RequestUri!.AbsolutePath;

            if (path == "/api/campaigns")
            {

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<ListPageResult<CampaignDto>>(
                                new ListPageResult<CampaignDto>([], false, null),
                                true,
                                null),
                            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto)));

            }

            if (path == "/api/workspaces")
            {

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<WorkspaceInfo[]>([], true, null),
                            ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray)));

            }

            if (path == "/api/models")
            {

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new ApiResponse<ModelInfoDto[]>([], true, null),
                            ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray)));

            }

            if (path == $"/api/sessions/{StaleSessionId:D}")
            {

                ApiResponse<SessionDetailDto> envelope =
                    ApiResponse<SessionDetailDto>.FromResult(
                        Result<SessionDetailDto>.Failure(
                            new Error(
                                ErrorCodes.Session.NotFound,
                                "Session was not found.")));

                return Task.FromResult(
                    Json(
                        JsonSerializer.SerializeToUtf8Bytes(
                            envelope,
                            ArcanumJsonContext.Default.ApiResponseSessionDetailDto),
                        HttpStatusCode.NotFound));

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

    private sealed class FakeContextStore(
        CliContextDocument? document = null) :
        ICliContextStore,
        ICliContextExclusiveWriter
    {

        private CliContextDocument _document =
            document ?? CliContextDocument.Empty;

        public int ExclusiveSaves { get; private set; }

        public int UnprotectedSaves { get; private set; }

        public bool ThrowOnLoad { get; init; }

        public string FilePath => "/tmp/cli-context.json";

        public CliContextDocument Load() =>
            ThrowOnLoad
                ? throw new InvalidOperationException(
                    "Saved context must not be read under --no-context.")
                : _document;

        public void Save(CliContextDocument value)
        {

            UnprotectedSaves++;

            _document = value;

        }

        public void SaveUnderExclusive(CliContextDocument value)
        {

            ExclusiveSaves++;

            _document = value;

        }

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

    private sealed class UnusedResourceCatalog : ICliResourceCatalog
    {

        public Task<ResourceSelectionResult<CampaignDto>> SelectCampaignAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<SessionSummaryDto>> SelectSessionAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<EntryDto>> SelectSessionEntryAsync(
            Guid sessionId,
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<WorkspaceInfo>> SelectWorkspaceAsync(
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

        public Task<ResourceSelectionResult<ModelInfoDto>> SelectModelAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<ProviderInfoDto>> SelectProviderAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        public Task<ResourceSelectionResult<McpServerInfo>> SelectMcpServerAsync(
            string? identifier,
            CancellationToken cancellationToken) => throw Unused();

        private static InvalidOperationException Unused() =>
            new("Context validation must not open a resource selector.");

    }

}
