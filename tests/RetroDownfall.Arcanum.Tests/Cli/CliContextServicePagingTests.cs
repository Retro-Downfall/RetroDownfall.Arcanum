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

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliContextServicePagingTests
{

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

        CliContextService service = new(
            new FakeContextStore(),
            new UnusedResourceCatalog(),
            client,
            Options.Create(new ArcanumSettings()),
            new CliSessionManager(new ConsoleDispatcher(new CliInvocationContext())));

        CliContextValidation validation = await service.ValidateAsync(
            noContext: false,
            CancellationToken.None);

        Assert.Equal(1, handler.CampaignRequests);

        Assert.Empty(validation.Campaigns);

    }

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

    private sealed class FakeContextStore : ICliContextStore
    {

        private CliContextDocument _document = CliContextDocument.Empty;

        public string FilePath => "/tmp/cli-context.json";

        public CliContextDocument Load() => _document;

        public void Save(CliContextDocument document) => _document = document;

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
