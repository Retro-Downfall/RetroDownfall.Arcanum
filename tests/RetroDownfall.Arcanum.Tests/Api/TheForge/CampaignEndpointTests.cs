using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

public sealed class CampaignEndpointTests
{

    [SkippableFact]
    public async Task RegisterCampaign_repository_failure_maps_error_code()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<ICampaignRepository>();

            services.AddScoped<ICampaignRepository, MaxReachedCampaignRepository>();
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        string path = Path.Combine(factory.TempHome, "campaign-max-result");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest request = new(
            "Rejected Campaign",
            path,
            WorkspaceType.Campaign,
            null);

        string payload = JsonSerializer.Serialize(
            request,
            ArcanumJsonContext.Default.RegisterCampaignRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/campaigns",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<CampaignDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Campaign.MaxReached, body.Error!.Value.Code);

        Assert.Equal(
            "Repository failure selected by code, not legacy exception text.",
            body.Error.Value.Message);

    }

    private sealed class MaxReachedCampaignRepository : ICampaignRepository
    {

        public Task<Campaign?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByPathAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<Campaign>.Failure(
                    new Error(
                        ErrorCodes.Campaign.MaxReached,
                        "Repository failure selected by code, not legacy exception text.")));

        public Task<Campaign> UpdateAsync(
            Campaign campaign,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

    }

}
