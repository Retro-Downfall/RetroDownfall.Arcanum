using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST/GET/DELETE /v1/files</c> — DESIGN.md §11.20.
/// </summary>
[Collection("ApiHost")]
public sealed class OpenAiV1FilesEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public OpenAiV1FilesEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostFiles_UploadListRetrieveDeleteContent_RoundTrips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        byte[] contentBytes = Encoding.UTF8.GetBytes("""{"model":"m","messages":[]}""" + "\n");

        OpenAiFileObject uploaded = await UploadAsync(client, "batch-input.jsonl", "application/jsonl", contentBytes, "batch");

        Assert.StartsWith("file-", uploaded.Id, StringComparison.Ordinal);

        Assert.Equal("batch-input.jsonl", uploaded.Filename);

        Assert.Equal("batch", uploaded.Purpose);

        Assert.Equal(contentBytes.Length, uploaded.Bytes);

        Guid storedId = Guid.ParseExact(uploaded.Id["file-".Length..], "N");
        byte[] storedBytes = await File.ReadAllBytesAsync(
            Path.Combine(
                _factory.TempHome,
                ".config",
                "arcanum",
                "files",
                storedId.ToString("N")));
        Assert.True(storedBytes.AsSpan().StartsWith("ARCABLOB"u8));
        Assert.NotEqual(contentBytes, storedBytes);

        // Retrieve metadata.
        HttpResponseMessage getResponse = await client.GetAsync($"/v1/files/{uploaded.Id}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        OpenAiFileObject? fetched = JsonSerializer.Deserialize(
            await getResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiFileObject);

        Assert.NotNull(fetched);

        Assert.Equal(uploaded.Id, fetched.Id);

        // List, filtered by purpose.
        HttpResponseMessage listResponse = await client.GetAsync("/v1/files?purpose=batch");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        OpenAiFileListResponse? listBody = JsonSerializer.Deserialize(
            await listResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiFileListResponse);

        Assert.NotNull(listBody);

        Assert.Contains(listBody.Data, f => f.Id == uploaded.Id);

        // Download content.
        HttpResponseMessage contentResponse = await client.GetAsync($"/v1/files/{uploaded.Id}/content");

        Assert.Equal(HttpStatusCode.OK, contentResponse.StatusCode);

        byte[] downloaded = await contentResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(contentBytes, downloaded);

        Assert.Equal("application/jsonl", contentResponse.Content.Headers.ContentType?.MediaType);

        Assert.NotNull(contentResponse.Content.Headers.ContentDisposition);

        Assert.Equal("attachment", contentResponse.Content.Headers.ContentDisposition!.DispositionType);

        // Delete.
        HttpResponseMessage deleteResponse = await client.DeleteAsync($"/v1/files/{uploaded.Id}");

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        OpenAiFileDeleteResponse? deleteBody = JsonSerializer.Deserialize(
            await deleteResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiFileDeleteResponse);

        Assert.NotNull(deleteBody);

        Assert.True(deleteBody.Deleted);

        // Now gone.
        HttpResponseMessage afterDelete = await client.GetAsync($"/v1/files/{uploaded.Id}");

        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

    }

    [SkippableFact]
    public async Task PostFiles_MissingPurpose_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes("hello"));

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        form.Add(fileContent, "file", "hello.txt");

        HttpResponseMessage response = await client.PostAsync("/v1/files", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task PostFiles_ExtensionMimeMismatch_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        // A .png claiming to be plain text — extension/declared-type mismatch.
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes("not actually a png"));

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        form.Add(fileContent, "file", "image.png");

        form.Add(new StringContent("assistants"), "purpose");

        HttpResponseMessage response = await client.PostAsync("/v1/files", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task PostFiles_OverlongFilename_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes("hello"));

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        string overlongName = new string('a', 300) + ".txt";

        form.Add(fileContent, "file", overlongName);

        form.Add(new StringContent("assistants"), "purpose");

        HttpResponseMessage response = await client.PostAsync("/v1/files", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetFile_UnknownId_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/v1/files/file-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetFile_MalformedId_Returns404NotThrow()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/v1/files/not-a-valid-id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task PostFiles_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();

        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes("hello"));

        form.Add(fileContent, "file", "hello.txt");

        form.Add(new StringContent("assistants"), "purpose");

        HttpResponseMessage response = await client.PostAsync("/v1/files", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    [Fact]
    public void UploadSizeBoundary_rejects_content_above_internal_limit()
    {
        long maxUploadBytes = OpenAiV1Endpoints.ResolveMaxUploadBytes();

        Assert.True(OpenAiV1Endpoints.IsUploadSizeAllowed(maxUploadBytes));
        Assert.False(OpenAiV1Endpoints.IsUploadSizeAllowed(maxUploadBytes + 1));
    }

    private static async Task<OpenAiFileObject> UploadAsync(
        HttpClient client,
        string filename,
        string contentType,
        byte[] bytes,
        string purpose)
    {

        using MultipartFormDataContent form = new();

        ByteArrayContent fileContent = new(bytes);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        form.Add(fileContent, "file", filename);

        form.Add(new StringContent(purpose), "purpose");

        HttpResponseMessage response = await client.PostAsync("/v1/files", form);

        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected Created, got {response.StatusCode}: {json}");

        OpenAiFileObject? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiFileObject);

        Assert.NotNull(body);

        return body;

    }

}
