using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class WardsEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public WardsEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetWards_WithValidApiKey_ReturnsWardListEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/wards");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WardDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWardDtoArray);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task GetWards_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/wards");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    // Issue #53: an auto-approved Ward is created and resolved in one atomic step, so it is never
    // listed as active and a manual resolver racing it loses with AlreadyResolved (409) rather than
    // silently replacing the automatic decision.
    [SkippableFact]
    public async Task ResolveWard_AfterAutomaticResolution_Returns409AndIsNeverListedAsActive()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        IWard ward = _factory.Services.GetRequiredService<IWard>();

        string wardId = $"auto-{Guid.NewGuid():N}";

        WardResolution automatic = ward.RecordAutomaticResolution(
            wardId,
            allowed: true,
            reason: "Auto-approved by operator policy",
            WardResolutionOrigin.AutoApproved);

        Assert.True(automatic.Allowed);

        Assert.Equal(WardResolutionOrigin.AutoApproved, automatic.Origin);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage listResponse = await client.GetAsync("/api/wards");

        ApiResponse<WardDto[]>? listed = JsonSerializer.Deserialize(
            await listResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseWardDtoArray);

        Assert.DoesNotContain(
            listed!.Data!,
            dto => string.Equals(dto.WardId, wardId, StringComparison.Ordinal));

        HttpResponseMessage resolveResponse = await client.PostAsJsonAsync(
            $"/api/wards/{wardId}",
            new ResolveWardRequest(false, "manual deny"),
            ArcanumJsonContext.Default.ResolveWardRequest);

        Assert.Equal(HttpStatusCode.Conflict, resolveResponse.StatusCode);

        ApiResponse<WardResolutionDto>? conflict = JsonSerializer.Deserialize(
            await resolveResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseWardResolutionDto);

        Assert.Equal("Ward.AlreadyResolved", conflict!.Error!.Value.Code);

    }

}
