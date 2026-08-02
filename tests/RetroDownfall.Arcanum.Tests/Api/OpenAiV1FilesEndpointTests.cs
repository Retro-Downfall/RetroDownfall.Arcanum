using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

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

        Assert.False(File.Exists(
            Path.Combine(
                _factory.TempHome,
                ".config",
                "arcanum",
                "files",
                storedId.ToString("N"))));

        // Now gone.
        HttpResponseMessage afterDelete = await client.GetAsync($"/v1/files/{uploaded.Id}");

        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

    }

    [Fact]

    public async Task PostFiles_WhenOwnedFilePublicationFails_ReturnsSanitizedInternalError()
    {

        await using ArcanumWebApplicationFactory isolatedFactory = new();

        isolatedFactory.ServiceOverrides = services =>
        {

            services.RemoveAll<IUploadedFileRepository>();

            services.AddScoped<IUploadedFileRepository>(
                _ => new DeleteStatusUploadedFileRepository(
                    UploadedFileDeleteStatus.NotFound,
                    new UploadedFilePublicationException(Guid.Empty)));

        };

        HttpClient client = isolatedFactory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        ByteArrayContent fileContent = new([1, 2, 3, 4]);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        form.Add(fileContent, "file", "publication.bin");

        form.Add(new StringContent("assistants"), "purpose");

        HttpResponseMessage response = await client.PostAsync("/v1/files", form);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(error);

        Assert.Equal("internal_error", error.Error.Code);

        Assert.Equal("Failed to store the uploaded file.", error.Error.Message);

        Assert.DoesNotContain(nameof(UploadedFilePublicationException), json, StringComparison.Ordinal);

    }

    [Fact]

    public async Task DeleteFile_WhenLifecycleNeedsRecovery_ReturnsOpenAi500WithoutDeletedTrue()
    {

        Guid id = Guid.NewGuid();

        await using ArcanumWebApplicationFactory isolatedFactory = new();

        isolatedFactory.ServiceOverrides = services =>
        {

            services.RemoveAll<IUploadedFileRepository>();

            services.AddScoped<IUploadedFileRepository>(
                _ => new DeleteStatusUploadedFileRepository(
                    UploadedFileDeleteStatus.RecoveryRequired));

        };

        HttpClient client = isolatedFactory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.DeleteAsync(
            "/v1/files/file-" + id.ToString("N"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(error);

        Assert.Equal("file_delete_recovery_required", error.Error.Code);

        Assert.DoesNotContain("\"deleted\":true", json, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task DeleteFile_ReferencedByTerminalBatch_Returns409AndPreservesContent()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        byte[] contentBytes = Encoding.UTF8.GetBytes("{}\n");

        OpenAiFileObject uploaded = await UploadAsync(
            client,
            "retained-input.jsonl",
            "application/jsonl",
            contentBytes,
            "batch");

        Guid fileId = Guid.ParseExact(uploaded.Id["file-".Length..], "N");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {

            IBatchRepository batches = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

            DateTimeOffset now = DateTimeOffset.UtcNow;

            await batches.CreateAsync(
                new BatchRecord(
                    Guid.NewGuid(),
                    fileId,
                    "/v1/chat/completions",
                    BatchStatuses.Completed,
                    now,
                    now,
                    null,
                    null),
                CancellationToken.None);

        }

        HttpResponseMessage response = await client.DeleteAsync($"/v1/files/{uploaded.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(error);

        Assert.Equal("file_referenced_by_batch", error.Error.Code);

        Assert.Equal("id", error.Error.Param);

        HttpResponseMessage contentResponse = await client.GetAsync($"/v1/files/{uploaded.Id}/content");

        Assert.Equal(HttpStatusCode.OK, contentResponse.StatusCode);

        Assert.Equal(contentBytes, await contentResponse.Content.ReadAsByteArrayAsync());

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

    private sealed class DeleteStatusUploadedFileRepository(
        UploadedFileDeleteStatus status,
        Exception? publicationException = null) : IUploadedFileRepository
    {

        public Task CreateAsync(
            UploadedFileRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateForOwnedFileAsync(
            UploadedFileRecord record,
            CancellationToken cancellationToken = default) =>
            throw publicationException ?? new NotSupportedException();

        public Task<UploadedFileRecord?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UploadedFileRecord?>(null);

        public Task<IReadOnlyList<UploadedFileRecord>> ListAsync(
            string? purpose,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UploadedFileRecord>>([]);

        public Task<UploadedFileDeleteStatus> TryDeleteUnreferencedAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

}
