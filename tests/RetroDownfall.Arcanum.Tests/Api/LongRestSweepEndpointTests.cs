using System.Net;

using System.Text;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Routing;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LongRestSweepEndpointTests
{
    private const string Path = "/api/memory/saga/long-rest/sweeps";

    private static readonly Guid CampaignId = Guid.Parse("7e1d9c42-05b8-4f63-9a0e-3c8b27d641f5");

    private static readonly CovenantDigest RootDigest = new(Enumerable.Repeat((byte)0x4A, 32).ToArray());

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticated_exact_scope_returns_the_server_resolved_content_free_operation(bool campaign)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SweepCoordinator coordinator = new();

        await using ArcanumWebApplicationFactory factory = Host(coordinator);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(Path, Json(campaign
            ? $$"""{"scope":"Campaign","campaignId":"{{CampaignId:D}}"}"""
            : """{"scope":"Global"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<LongRunningOperationDto>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);

        Assert.NotNull(envelope);

        Assert.True(envelope.IsSuccess);

        Assert.False(string.IsNullOrWhiteSpace(envelope.TraceId));

        Assert.Equal(coordinator.Operation, envelope.Data);

        CanonicalCampaignContext scope = Assert.Single(coordinator.Requests);

        Assert.Equal(campaign ? CampaignId : (Guid?)null, scope.CampaignId);

        Assert.Equal(campaign ? SessionCampaignBindingKind.Campaign : SessionCampaignBindingKind.GlobalOnly, scope.Binding.Kind);

        Assert.Equal(campaign ? (long?)12 : null, scope.CampaignAvailabilityGeneration);

        Assert.Equal(campaign ? (long?)7 : null, scope.PathIdentityRevision);

        Assert.Equal(campaign ? RootDigest : (CovenantDigest?)null, scope.RootIdentityDigest);

        Assert.All(coordinator.Partitions, partition => Assert.Null(partition.ProtectedPartitionDigest));

        Assert.Equal(0, coordinator.Resumes);

        RouteEndpoint route = Assert.Single(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>(), endpoint => endpoint.RoutePattern.RawText == Path);

        Assert.False(string.IsNullOrWhiteSpace(route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        AssertContentFree(body.RootElement);
    }

    [SkippableFact]
    public async Task Unauthenticated_sweep_refuses_before_resolution_or_operation_admission()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SweepCoordinator coordinator = new();

        Availability availability = new();

        await using ArcanumWebApplicationFactory factory = Host(coordinator, availability: availability);

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(Path, Json("""{"scope":"Global"}"""));

        await AssertRefusalAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Auth.Unauthorized);

        Assert.Empty(coordinator.Requests);

        Assert.Equal(0, availability.Reads);
    }

    [SkippableFact]
    public async Task Unauthenticated_malformed_sweep_refuses_before_body_parsing_resolution_or_operation_admission()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SweepCoordinator coordinator = new();

        RefusingResolver resolver = new();

        await using ArcanumWebApplicationFactory factory = Host(coordinator);

        Action<IServiceCollection>? configureHost = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            configureHost?.Invoke(services);

            services.RemoveAll<ICanonicalCampaignContextResolver>();

            services.AddScoped<ICanonicalCampaignContextResolver>(_ => resolver);
        };

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(Path, Json("{ not json"));

        await AssertRefusalAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Auth.Unauthorized);

        Assert.Equal(0, resolver.Resolutions);

        Assert.Empty(coordinator.Requests);

        Assert.Empty(coordinator.Partitions);

        Assert.Equal(0, coordinator.Resumes);
    }

    [SkippableTheory]
    [InlineData("{}", "LongRest.InvalidRequest")]
    [InlineData("{\"scope\":\"Campaign\"}", "LongRest.InvalidRequest")]
    [InlineData("{\"scope\":\"Campaign\",\"campaignId\":\"00000000-0000-0000-0000-000000000000\"}", "LongRest.InvalidRequest")]
    [InlineData("{\"scope\":\"Global\",\"campaignId\":\"7e1d9c42-05b8-4f63-9a0e-3c8b27d641f5\"}", "LongRest.InvalidRequest")]
    [InlineData("{\"scope\":\"Installation\"}", "Validation.InvalidBody")]
    [InlineData("{\"scope\":1}", "Validation.InvalidBody")]
    [InlineData("{\"scope\":null}", "Validation.InvalidBody")]
    [InlineData("{\"scope\":{}}", "Validation.InvalidBody")]
    [InlineData("{\"scope\":\"Global\",\"campaignId\":\"not-a-guid\"}", "Validation.InvalidBody")]
    [InlineData("null", "Validation.InvalidBody")]
    [InlineData("{ not json", "Validation.InvalidBody")]
    public async Task Invalid_sweep_body_refuses_without_admitting_an_operation(string payload, string code)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SweepCoordinator coordinator = new();

        await using ArcanumWebApplicationFactory factory = Host(coordinator);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(Path, Json(payload));

        await AssertRefusalAsync(response, HttpStatusCode.BadRequest, code);

        Assert.Empty(coordinator.Requests);
    }

    [SkippableTheory]
    [InlineData("text/plain")]
    [InlineData("application/json; charset=unknown-long-rest-charset")]
    public async Task Unsupported_sweep_media_returns_the_normal_typed_body_refusal(string mediaType)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SweepCoordinator coordinator = new();

        await using ArcanumWebApplicationFactory factory = Host(coordinator);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using StringContent content = Json("""{"scope":"Global"}""");

        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType);

        using HttpResponseMessage response = await client.PostAsync(Path, content);

        await AssertRefusalAsync(response, HttpStatusCode.UnsupportedMediaType, ErrorCodes.Validation.UnsupportedMediaType);

        Assert.Empty(coordinator.Requests);
    }

    [SkippableFact]
    public async Task Disabled_sweep_returns_unavailable_without_resuming_work()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SweepCoordinator coordinator = new(ErrorCodes.LongRest.Unavailable);

        await using ArcanumWebApplicationFactory factory = Host(coordinator, enabled: false);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(Path, Json("""{"scope":"Global"}"""));

        await AssertRefusalAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.LongRest.Unavailable);

        Assert.Equal(0, coordinator.Resumes);
    }

    [SkippableFact]
    public async Task Stale_generation_returns_conflict_without_resuming_work()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SweepCoordinator coordinator = new(ErrorCodes.LongRest.StaleInput);

        await using ArcanumWebApplicationFactory factory = Host(coordinator);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(Path, Json("""{"scope":"Global"}"""));

        await AssertRefusalAsync(response, HttpStatusCode.Conflict, ErrorCodes.LongRest.StaleInput);

        Assert.Single(coordinator.Requests);

        Assert.Equal(0, coordinator.Resumes);
    }

    [SkippableFact]
    public async Task Missing_canonical_campaign_refuses_before_operation_admission()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SweepCoordinator coordinator = new();

        await using ArcanumWebApplicationFactory factory = Host(coordinator, availability: new Availability { Generation = null });

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync(Path, Json(
            $$"""{"scope":"Campaign","campaignId":"{{CampaignId:D}}"}"""));

        await AssertRefusalAsync(response, HttpStatusCode.NotFound, ErrorCodes.Campaign.NotFound);

        Assert.Empty(coordinator.Requests);
    }

    [Fact]
    public void Sweep_wire_contract_can_round_trip_only_scope_and_campaign_identity_with_generated_metadata()
    {
        JsonTypeInfo? metadata = ArcanumJsonContext.Default.GetTypeInfo(typeof(LongRestSweepRequest));

        Assert.NotNull(metadata);

        string json = JsonSerializer.Serialize(new LongRestSweepRequest(LongRestSweepScopeKind.Global, null), metadata);

        using JsonDocument body = JsonDocument.Parse(json);

        Assert.Equal("Global", body.RootElement.GetProperty("scope").GetString());

        Assert.Equal(new[] { "campaignId", "scope" }, body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());

        Assert.Equal(new LongRestSweepRequest(LongRestSweepScopeKind.Global, null),
            JsonSerializer.Deserialize(json, metadata));

        Assert.NotNull(ArcanumJsonContext.Default.GetTypeInfo(typeof(ApiResponse<LongRunningOperationDto>)));
    }

    private static ArcanumWebApplicationFactory Host(SweepCoordinator coordinator, bool enabled = true, Availability? availability = null) => new()
    {
        SettingsOverride = settings => settings with
        {
            Features = settings.Features with { LongRest = enabled, CampaignRollups = false },
        },

        ServiceOverrides = services =>
        {
            services.RemoveAll<ILongRestSweepCoordinator>();

            services.AddScoped<ILongRestSweepCoordinator>(_ => coordinator);

            services.RemoveAll<ISessionCampaignBindingReader>();

            services.AddScoped<ISessionCampaignBindingReader>(_ => new Bindings());

            services.RemoveAll<ICampaignPathIdentityReader>();

            services.AddScoped<ICampaignPathIdentityReader>(_ => new Paths());

            services.RemoveAll<ICampaignAvailabilityReader>();

            services.AddScoped<ICampaignAvailabilityReader>(_ => availability ?? new Availability());
        },
    };

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static async Task AssertRefusalAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());

        Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));

        AssertContentFree(body.RootElement);
    }

    private static void AssertContentFree(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, new[]
                {
                    "content", "summary", "vector", "embedding", "checkpointPayload", "checkpointReference",
                    "protectedPartitionDigest", "capability", "authority", "claimLease", "rootIdentityDigest", "workingDirectory",
                });

                AssertContentFree(property.Value);
            }
        }
        else if (value.ValueKind is JsonValueKind.Array)
        {
            foreach (JsonElement element in value.EnumerateArray())
            {
                AssertContentFree(element);
            }
        }
    }

    private sealed class SweepCoordinator(string? failure = null) : ILongRestSweepCoordinator
    {
        public List<CanonicalCampaignContext> Requests { get; } = [];

        public List<LongRestSweepScope> Partitions { get; } = [];

        public int Resumes { get; private set; }

        public LongRunningOperationDto Operation { get; } = new(
            Guid.Parse("12b94f1a-9a76-4776-9d83-63f2efab172e"), LongRunningOperationKinds.LongRestSweep,
            LongRunningOperationState.Pending, LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            null, null, null, null, null, null, null, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            null, null, null, null, null, 0, 0, false, "Long Rest requested.", null, 0);

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct)
        {
            Requests.Add(scope);

            return Task.FromResult(failure is null ? Result<LongRunningOperationDto>.Success(Operation)
                : Result<LongRunningOperationDto>.Failure(new Error(failure, "The bounded sweep was refused.")));
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct)
        {
            Partitions.Add(partition);

            return RequestAsync(scope, ct);
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease authority, CancellationToken ct) =>
            RequestAsync(scope, partition, ct);

        public Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            Resumes++;

            throw new InvalidOperationException("An operator management request cannot execute protected maintenance.");
        }
    }

    private sealed class RefusingResolver : ICanonicalCampaignContextResolver
    {
        public int Resolutions { get; private set; }

        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken cancellationToken)
        {
            Resolutions++;

            return ValueTask.FromResult(Result<CanonicalCampaignContext>.Failure(
                new Error(ErrorCodes.Campaign.NotFound, "Authentication must precede canonical resolution.")));
        }
    }

    private sealed class Bindings : ISessionCampaignBindingReader
    {
        public ValueTask<Result<SessionCampaignBindingRecord?>> FindAsync(Guid? sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionCampaignBindingRecord?>.Success(null));
    }

    private sealed class Availability : ICampaignAvailabilityReader
    {
        public long? Generation { get; init; } = 12;

        public int Reads { get; private set; }

        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(Guid campaignId, CancellationToken cancellationToken)
        {
            Reads++;

            Assert.Equal(CampaignId, campaignId);

            return ValueTask.FromResult(Result<long?>.Success(Generation));
        }
    }

    private sealed class Paths : ICampaignPathIdentityReader
    {
        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? workingDirectory, CancellationToken cancellationToken)
        {
            Assert.True(string.IsNullOrWhiteSpace(workingDirectory));

            return ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(null));
        }

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
        {
            Assert.Equal(CampaignId, campaignId);

            return ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(new(CampaignId, 1, 7, 2, RootDigest)));
        }
    }
}
