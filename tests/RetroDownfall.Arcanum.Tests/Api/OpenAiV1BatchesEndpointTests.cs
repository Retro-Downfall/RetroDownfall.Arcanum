using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST/GET /v1/batches</c>, <c>GET /v1/batches/{id}</c>, <c>POST /v1/batches/{id}/cancel</c> —
/// DESIGN.md §11.21. Covers metadata CRUD only; <c>BatchProcessingServiceTests</c> covers the
/// background JSONL processor.
/// </summary>
[Collection("ApiHost")]
public sealed class OpenAiV1BatchesEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public OpenAiV1BatchesEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostBatches_ValidInputFile_CreatesValidatingBatch()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiFileObject inputFile = await UploadInputFileAsync(client, """{"custom_id":"1","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""" + "\n");

        OpenAiBatchObject batch = await CreateBatchAsync(client, inputFile.Id);

        Assert.StartsWith("batch_", batch.Id, StringComparison.Ordinal);

        Assert.Equal("validating", batch.Status);

        Assert.Equal("/v1/chat/completions", batch.Endpoint);

        Assert.Equal(inputFile.Id, batch.InputFileId);

        Assert.Null(batch.OutputFileId);

        Assert.Null(batch.ErrorFileId);

    }

    [SkippableFact]
    public async Task PostBatches_UnsupportedEndpoint_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiFileObject inputFile = await UploadInputFileAsync(client, "{}");

        HttpResponseMessage response = await client.PostAsync(
            "/v1/batches",
            JsonContentOf(new OpenAiBatchRequest(inputFile.Id, "/v1/embeddings")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task PostBatches_UnknownInputFile_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/batches",
            JsonContentOf(new OpenAiBatchRequest($"file-{Guid.NewGuid():N}", "/v1/chat/completions")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task PostBatches_MissingInputFileId_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/batches",
            JsonContentOf(new OpenAiBatchRequest(null, "/v1/chat/completions")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetBatch_AfterCreate_ReturnsSameBatch()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiFileObject inputFile = await UploadInputFileAsync(client, "{}");

        OpenAiBatchObject created = await CreateBatchAsync(client, inputFile.Id);

        HttpResponseMessage response = await client.GetAsync($"/v1/batches/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        OpenAiBatchObject? fetched = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiBatchObject);

        Assert.NotNull(fetched);

        Assert.Equal(created.Id, fetched.Id);

    }

    [SkippableFact]
    public async Task GetBatch_UnknownId_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/v1/batches/batch_{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetBatch_MalformedId_Returns404NotThrow()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/v1/batches/not-a-valid-id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task ListBatches_FiltersByStatus()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiFileObject inputFile = await UploadInputFileAsync(client, "{}");

        OpenAiBatchObject created = await CreateBatchAsync(client, inputFile.Id);

        HttpResponseMessage response = await client.GetAsync("/v1/batches?status=validating");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        OpenAiBatchListResponse? list = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiBatchListResponse);

        Assert.NotNull(list);

        Assert.Contains(list.Data, b => b.Id == created.Id);

    }

    [SkippableFact]
    public async Task CancelBatch_StillValidating_MarksCancelled()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiFileObject inputFile = await UploadInputFileAsync(client, "{}");

        OpenAiBatchObject created = await CreateBatchAsync(client, inputFile.Id);

        HttpResponseMessage response = await client.PostAsync($"/v1/batches/{created.Id}/cancel", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        OpenAiBatchObject? cancelled = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiBatchObject);

        Assert.NotNull(cancelled);

        Assert.Equal("cancelled", cancelled.Status);

    }

    [SkippableFact]
    public async Task CancelBatch_AlreadyCancelled_IsIdempotent()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiFileObject inputFile = await UploadInputFileAsync(client, "{}");

        OpenAiBatchObject created = await CreateBatchAsync(client, inputFile.Id);

        _ = await client.PostAsync($"/v1/batches/{created.Id}/cancel", content: null);

        HttpResponseMessage secondCancel = await client.PostAsync($"/v1/batches/{created.Id}/cancel", content: null);

        Assert.Equal(HttpStatusCode.OK, secondCancel.StatusCode);

        OpenAiBatchObject? cancelled = JsonSerializer.Deserialize(
            await secondCancel.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiBatchObject);

        Assert.NotNull(cancelled);

        Assert.Equal("cancelled", cancelled.Status);

    }

    [SkippableFact]
    public async Task PostBatches_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/batches",
            JsonContentOf(new OpenAiBatchRequest("file-abc", "/v1/chat/completions")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    [SkippableFact]
    public async Task ResetBatch_StuckInProgressWithInputFileResetsToValidating()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiFileObject inputFile = await UploadInputFileAsync(client, "{}");

        OpenAiBatchObject created = await CreateBatchAsync(client, inputFile.Id);

        using IServiceScope scope = _factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        IBatchRepository batches = new BatchRepository(db);

        BatchRecord record = await batches.GetByIdAsync(Guid.Parse(created.Id.AsSpan(6)), CancellationToken.None) ?? throw new InvalidOperationException("Batch not found");

        await batches.UpdateStatusAsync(record.Id, BatchStatuses.InProgress, null, record.OutputFileId, record.ErrorFileId, CancellationToken.None);

        HttpResponseMessage response = await client.PostAsync($"/v1/batches/{created.Id}/reset", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        OpenAiBatchObject? reset = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiBatchObject);

        Assert.NotNull(reset);

        Assert.Equal("validating", reset.Status);

    }

    [SkippableFact]
    public async Task ResetBatch_ValidatingBatch_Returns409()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        OpenAiFileObject inputFile = await UploadInputFileAsync(client, "{}");

        OpenAiBatchObject created = await CreateBatchAsync(client, inputFile.Id);

        HttpResponseMessage response = await client.PostAsync($"/v1/batches/{created.Id}/reset", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

    }

    [SkippableFact]
    public async Task ResetBatch_UnknownId_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync($"/v1/batches/batch_{Guid.NewGuid():N}/reset", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    private static StringContent JsonContentOf(OpenAiBatchRequest request) =>
        new(JsonSerializer.Serialize(request, ArcanumJsonContext.Default.OpenAiBatchRequest), Encoding.UTF8, "application/json");

    private static async Task<OpenAiBatchObject> CreateBatchAsync(HttpClient client, string inputFileId)
    {

        HttpResponseMessage response = await client.PostAsync(
            "/v1/batches",
            JsonContentOf(new OpenAiBatchRequest(inputFileId, "/v1/chat/completions")));

        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected OK, got {response.StatusCode}: {json}");

        OpenAiBatchObject? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiBatchObject);

        Assert.NotNull(body);

        return body;

    }

    private static async Task<OpenAiFileObject> UploadInputFileAsync(HttpClient client, string jsonlContent)
    {

        using MultipartFormDataContent form = new();

        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(jsonlContent));

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");

        form.Add(fileContent, "file", "batch-input.jsonl");

        form.Add(new StringContent("batch"), "purpose");

        HttpResponseMessage response = await client.PostAsync("/v1/files", form);

        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected Created, got {response.StatusCode}: {json}");

        OpenAiFileObject? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiFileObject);

        Assert.NotNull(body);

        return body;

    }

}
