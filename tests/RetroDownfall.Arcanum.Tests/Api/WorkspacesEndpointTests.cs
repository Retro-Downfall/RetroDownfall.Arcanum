using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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

    [SkippableFact]
    public async Task PutFileContents_CreatesFile_Returns200WithFileWriteResult()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, "put-write");

        HttpResponseMessage response = await client.PutAsync(
            $"/api/workspaces/{workspace.Id}/files/contents?relativePath=new.txt",
            JsonContent(new FileWriteRequest("hello from PUT"), ArcanumJsonContext.Default.FileWriteRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<FileWriteResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseFileWriteResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.True(body.Data!.BytesWritten > 0);

        Assert.Equal("hello from PUT", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "new.txt")));

    }

    [SkippableFact]
    public async Task PatchFileContents_ReplacesText_Returns200WithTextBlockReplaceResult()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, "patch-write");

        await client.PutAsync(
            $"/api/workspaces/{workspace.Id}/files/contents?relativePath=patch.txt",
            JsonContent(new FileWriteRequest("hello world"), ArcanumJsonContext.Default.FileWriteRequest));

        HttpRequestMessage patchRequest = new(HttpMethod.Patch, $"/api/workspaces/{workspace.Id}/files/contents?relativePath=patch.txt")
        {
            Content = JsonContent(new TextBlockReplaceRequest("world", "galaxy"), ArcanumJsonContext.Default.TextBlockReplaceRequest),
        };

        HttpResponseMessage response = await client.SendAsync(patchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<TextBlockReplaceResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseTextBlockReplaceResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.Equal(1, body.Data!.Replacements);

        Assert.Equal("hello galaxy", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "patch.txt")));

    }

    [SkippableFact]
    public async Task DeleteFile_RemovesFile_Returns200WithFileDeleteResult()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, "delete-write");

        await client.PutAsync(
            $"/api/workspaces/{workspace.Id}/files/contents?relativePath=todelete.txt",
            JsonContent(new FileWriteRequest("bye"), ArcanumJsonContext.Default.FileWriteRequest));

        HttpResponseMessage response = await client.DeleteAsync(
            $"/api/workspaces/{workspace.Id}/files?relativePath=todelete.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<FileDeleteResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseFileDeleteResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.False(body.Data!.WasDirectory);

        Assert.False(File.Exists(Path.Combine(workspace.Path, "todelete.txt")));

    }

    [SkippableFact]
    public async Task PostDirectory_CreatesDirectory_Returns201WithDirectoryCreateResult()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, "mkdir-write");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/workspaces/{workspace.Id}/files/directory?relativePath=newdir/nested",
            content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<DirectoryCreateResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseDirectoryCreateResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.True(Directory.Exists(Path.Combine(workspace.Path, "newdir", "nested")));

    }

    [SkippableFact]
    public async Task WriteEndpoints_UnknownWorkspace_Return404Envelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string unknownId = Guid.NewGuid().ToString("N");

        HttpResponseMessage putResponse = await client.PutAsync(
            $"/api/workspaces/{unknownId}/files/contents?relativePath=x.txt",
            JsonContent(new FileWriteRequest("x"), ArcanumJsonContext.Default.FileWriteRequest));

        await AssertEnvelopeErrorAsync(putResponse, HttpStatusCode.NotFound, "Workspace.NotFound", ArcanumJsonContext.Default.ApiResponseFileWriteResult);

        HttpRequestMessage patchRequest = new(HttpMethod.Patch, $"/api/workspaces/{unknownId}/files/contents?relativePath=x.txt")
        {
            Content = JsonContent(new TextBlockReplaceRequest("a", "b"), ArcanumJsonContext.Default.TextBlockReplaceRequest),
        };

        HttpResponseMessage patchResponse = await client.SendAsync(patchRequest);

        await AssertEnvelopeErrorAsync(patchResponse, HttpStatusCode.NotFound, "Workspace.NotFound", ArcanumJsonContext.Default.ApiResponseTextBlockReplaceResult);

        HttpResponseMessage deleteResponse = await client.DeleteAsync($"/api/workspaces/{unknownId}/files?relativePath=x.txt");

        await AssertEnvelopeErrorAsync(deleteResponse, HttpStatusCode.NotFound, "Workspace.NotFound", ArcanumJsonContext.Default.ApiResponseFileDeleteResult);

        HttpResponseMessage postResponse = await client.PostAsync(
            $"/api/workspaces/{unknownId}/files/directory?relativePath=x",
            content: null);

        await AssertEnvelopeErrorAsync(postResponse, HttpStatusCode.NotFound, "Workspace.NotFound", ArcanumJsonContext.Default.ApiResponseDirectoryCreateResult);

    }

    [SkippableFact]
    public async Task PutFileContents_MissingRelativePath_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, "missing-relpath");

        HttpResponseMessage response = await client.PutAsync(
            $"/api/workspaces/{workspace.Id}/files/contents",
            JsonContent(new FileWriteRequest("x"), ArcanumJsonContext.Default.FileWriteRequest));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task HeadFileContents_ExistingFile_ReturnsSizeAndLastModifiedHeaders()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, "head-existing");

        string content = "hello, head check";

        HttpResponseMessage putResponse = await client.PutAsync(
            $"/api/workspaces/{workspace.Id}/files/contents?relativePath=head.txt",
            JsonContent(new FileWriteRequest(content), ArcanumJsonContext.Default.FileWriteRequest));

        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        HttpRequestMessage headRequest = new(HttpMethod.Head, $"/api/workspaces/{workspace.Id}/files/contents?relativePath=head.txt");
        HttpResponseMessage headResponse = await client.SendAsync(headRequest);

        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);

        Assert.True(headResponse.Content.Headers.ContentLength.HasValue);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), headResponse.Content.Headers.ContentLength.Value);

        Assert.NotNull(headResponse.Content.Headers.LastModified);

        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());

    }

    [SkippableFact]
    public async Task HeadFileContents_UnknownFile_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, "head-missing");

        HttpRequestMessage headRequest = new(HttpMethod.Head, $"/api/workspaces/{workspace.Id}/files/contents?relativePath=missing.txt");
        HttpResponseMessage headResponse = await client.SendAsync(headRequest);

        Assert.Equal(HttpStatusCode.NotFound, headResponse.StatusCode);

    }

    [SkippableFact]
    public async Task HeadFileContents_UnknownWorkspace_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string unknownId = Guid.NewGuid().ToString("N");

        HttpRequestMessage headRequest = new(HttpMethod.Head, $"/api/workspaces/{unknownId}/files/contents?relativePath=x.txt");
        HttpResponseMessage headResponse = await client.SendAsync(headRequest);

        Assert.Equal(HttpStatusCode.NotFound, headResponse.StatusCode);

    }

    [SkippableFact]
    public async Task WriteEndpoints_ToggleDisabled_Return403Envelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // A dedicated, independently-constructed factory (not derived from the shared `_factory`) is required
        // here: building two hosts from the SAME ArcanumWebApplicationFactory instance (e.g. via
        // WithWebHostBuilder after already touching `_factory.Services`) makes both hosts try to seed the same
        // Grimoire database file, which collides. SettingsOverride flips the toggle before this factory's single
        // host is built.
        await using ArcanumWebApplicationFactory disabledFactory = new()
        {
            SettingsOverride = settings => settings with
            {
                Workspaces = settings.Workspaces with { EnableFileWrite = false },
            },
        };

        HttpClient client = disabledFactory.CreateAuthenticatedClient();

        WorkspaceInfo workspace = await RegisterWorkspaceAsync(client, "toggle-off", disabledFactory.TempHome);

        HttpResponseMessage putResponse = await client.PutAsync(
            $"/api/workspaces/{workspace.Id}/files/contents?relativePath=x.txt",
            JsonContent(new FileWriteRequest("x"), ArcanumJsonContext.Default.FileWriteRequest));

        await AssertEnvelopeErrorAsync(putResponse, HttpStatusCode.Forbidden, "Workspace.FileWriteDisabled", ArcanumJsonContext.Default.ApiResponseFileWriteResult);

        HttpRequestMessage patchRequest = new(HttpMethod.Patch, $"/api/workspaces/{workspace.Id}/files/contents?relativePath=x.txt")
        {
            Content = JsonContent(new TextBlockReplaceRequest("a", "b"), ArcanumJsonContext.Default.TextBlockReplaceRequest),
        };

        HttpResponseMessage patchResponse = await client.SendAsync(patchRequest);

        await AssertEnvelopeErrorAsync(patchResponse, HttpStatusCode.Forbidden, "Workspace.FileWriteDisabled", ArcanumJsonContext.Default.ApiResponseTextBlockReplaceResult);

        HttpResponseMessage deleteResponse = await client.DeleteAsync($"/api/workspaces/{workspace.Id}/files?relativePath=x.txt");

        await AssertEnvelopeErrorAsync(deleteResponse, HttpStatusCode.Forbidden, "Workspace.FileWriteDisabled", ArcanumJsonContext.Default.ApiResponseFileDeleteResult);

        HttpResponseMessage postResponse = await client.PostAsync(
            $"/api/workspaces/{workspace.Id}/files/directory?relativePath=x",
            content: null);

        await AssertEnvelopeErrorAsync(postResponse, HttpStatusCode.Forbidden, "Workspace.FileWriteDisabled", ArcanumJsonContext.Default.ApiResponseDirectoryCreateResult);

    }

    private Task<WorkspaceInfo> RegisterWorkspaceAsync(HttpClient client, string suffix) =>
        RegisterWorkspaceAsync(client, suffix, _factory.TempHome);

    private static async Task<WorkspaceInfo> RegisterWorkspaceAsync(HttpClient client, string suffix, string tempHome)
    {

        string root = Path.Combine(tempHome, $"workspace-{suffix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        CreateWorkspaceRequest request = new(Name: $"test-{suffix}-{Guid.NewGuid():N}", Path: root, Type: WorkspaceType.Custom);

        HttpResponseMessage response = await client.PostAsync(
            "/api/workspaces",
            JsonContent(request, ArcanumJsonContext.Default.CreateWorkspaceRequest));

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WorkspaceInfo>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWorkspaceInfo);

        return body!.Data!;

    }

    private static StringContent JsonContent<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new(JsonSerializer.Serialize(value, typeInfo), Encoding.UTF8, "application/json");

    private static async Task AssertEnvelopeErrorAsync<T>(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedErrorCode,
        JsonTypeInfo<ApiResponse<T>> typeInfo)
    {

        string json = await response.Content.ReadAsStringAsync();

        Assert.True(expectedStatus == response.StatusCode, $"Expected {expectedStatus} but got {response.StatusCode}. Body: {json}");

        ApiResponse<T>? body = JsonSerializer.Deserialize(json, typeInfo);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(expectedErrorCode, body.Error?.Code);

    }

}
