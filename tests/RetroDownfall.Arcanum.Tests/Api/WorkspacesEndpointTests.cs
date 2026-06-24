using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class WorkspacesEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public WorkspacesEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetWorkspaces_WithValidApiKey_ReturnsWorkspaceListEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/workspaces");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceInfo[]>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task PostWorkspaces_EmptyName_Returns400Envelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CreateWorkspaceRequest request = new(Name: "   ", Path: "/tmp", Type: WorkspaceType.Custom);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateWorkspaceRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/workspaces",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceInfo>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseWorkspaceInfo);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Workspace.NameEmpty", body.Error?.Code);

    }

}
