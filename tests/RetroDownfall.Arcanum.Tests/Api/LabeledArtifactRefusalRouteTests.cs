using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// What the direct-deletion routes answer when the labelled-artifact guard refuses a raw delete.
/// </summary>
/// <remarks>
/// <para>The Saga store's and the Entry repository's deletes return no <c>Result</c> of their own, so the
/// guard's refusal reaches the route as a typed exception, and the embeddings reset raises the same one
/// for the refusals its purge walk returns. Each route turns the exception's own <c>Error</c> back into
/// the envelope and status the central mapper gives that code, so a label table that cannot be read
/// answers <c>503 Covenant.Unavailable</c>. Before the exception was typed these answered <c>500
/// Hub.Unhandled</c>, and the embeddings route answered <c>Covenant.ManualArtifactErasureRequired</c>,
/// which tells an operator to erase an artifact by hand when the action is to repair the Grimoire
/// (§10.20.2).</para>
///
/// <para>The Saga and Entry cases put a guard that cannot read the label table in front of the store, with
/// everything else the host's own: the purge dispatch that runs first reads the table through the real
/// ledger and finds nothing labelled, which is the race the in-transaction check exists for.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class LabeledArtifactRefusalRouteTests
{
    private const int TestDimensions = 64;

    [SkippableFact]
    public async Task Deleting_one_Saga_memory_whose_labels_cannot_be_read_answers_503_and_keeps_the_row()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Host(static services =>
            services.AddScoped<ICovenantLabeledArtifactTransactionGuard>(
                static _ => new UnreadableLabeledArtifactGuard()));

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid memoryId = await InsertSagaAsync(factory);

        using HttpResponseMessage response = await client.DeleteAsync($"/api/saga/{memoryId:D}");

        ApiResponse<string> body = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseString);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, body.Error?.Code);

        Assert.Equal(1, await CountAsync(factory, "saga_memories", "Id", memoryId));
    }

    [SkippableFact]
    public async Task Deleting_every_Saga_memory_whose_labels_cannot_be_read_answers_503_and_keeps_the_rows()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Host(static services =>
            services.AddScoped<ICovenantLabeledArtifactTransactionGuard>(
                static _ => new UnreadableLabeledArtifactGuard()));

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid first = await InsertSagaAsync(factory);

        Guid second = await InsertSagaAsync(factory);

        using HttpResponseMessage response = await client.DeleteAsync("/api/saga?confirm=true");

        ApiResponse<string> body = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseString);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, body.Error?.Code);

        Assert.Equal(1, await CountAsync(factory, "saga_memories", "Id", first));

        Assert.Equal(1, await CountAsync(factory, "saga_memories", "Id", second));
    }

    [SkippableFact]
    public async Task Deleting_an_Entry_whose_labels_cannot_be_read_answers_503_and_keeps_the_Entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Host(static services =>
            services.AddScoped<ICovenantLabeledArtifactTransactionGuard>(
                static _ => new UnreadableLabeledArtifactGuard()));

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        Guid entryId = await AppendAssistantEntryAsync(client, sessionId);

        using HttpResponseMessage response = await client.DeleteAsync(
            $"/api/sessions/{sessionId:D}/entries/{entryId:D}");

        ApiResponse<bool> body = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, body.Error?.Code);

        Assert.Equal(1, await CountAsync(factory, "Entries", "Id", entryId));
    }

    /// <summary>
    /// An embeddings reset whose truncating transaction cannot read the labels answers 503
    /// <c>Covenant.Unavailable</c> and truncates nothing.
    /// </summary>
    /// <remarks>
    /// The purge walk reads the label table directly and finds nothing labelled, so the reset reaches the
    /// transaction that runs the set-based deletes; the guard asked there is the one that cannot read.
    /// </remarks>
    [SkippableTheory]
    [InlineData("saga")]
    [InlineData("entry")]
    [InlineData("all")]
    public async Task An_embeddings_reset_whose_truncation_cannot_read_the_labels_answers_503_and_keeps_the_rows(
        string scope)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Host(static services =>
            services.AddScoped<ICovenantLabeledArtifactTransactionGuard>(
                static _ => new UnreadableLabeledArtifactGuard()));

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid memoryId = await InsertSagaAsync(factory);

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/embeddings/reset?confirm=true&scope={scope}",
            null);

        ApiResponse<EmbeddingsResetResult> body = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseEmbeddingsResetResult);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, body.Error?.Code);

        Assert.Equal(1, await CountAsync(factory, "saga_memories", "Id", memoryId));
    }

    /// <summary>
    /// An embeddings reset stopped by a refusal from the purge walk answers that refusal's own code and the
    /// status the central mapper gives it, and truncates nothing.
    /// </summary>
    /// <remarks>
    /// The route used to answer every such stop as <c>Covenant.ManualArtifactErasureRequired</c>, whatever
    /// had actually happened. The two cases differ in status as well as code, so a route that kept a fixed
    /// status and swapped only the code would pass one of them and fail the other.
    /// </remarks>
    [SkippableTheory]
    [InlineData(ErrorCodes.Covenant.Unavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ErrorCodes.Covenant.StaleSnapshot, HttpStatusCode.Conflict)]
    public async Task An_embeddings_reset_stopped_by_a_purge_refusal_answers_that_refusals_code(
        string code,
        HttpStatusCode status)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Host(services =>
            services.AddScoped<ICovenantSensitiveArtifactPurger>(
                _ => new RefusingSensitivePurger(new Error(code, "The purge refused."))));

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid memoryId = await InsertSagaAsync(factory);

        await LabelSagaAsync(factory, memoryId);

        using HttpResponseMessage response = await client.PostAsync("/api/embeddings/reset?confirm=true&scope=saga", null);

        ApiResponse<EmbeddingsResetResult> body = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseEmbeddingsResetResult);

        Assert.Equal(status, response.StatusCode);

        Assert.False(body.IsSuccess);

        Assert.Equal(code, body.Error?.Code);

        Assert.Equal(1, await CountAsync(factory, "saga_memories", "Id", memoryId));
    }

    /// <summary>
    /// A host with memory management and the Saga on, and the supplied registrations in front of the
    /// composition's own.
    /// </summary>
    private static ArcanumWebApplicationFactory Host(Action<IServiceCollection> overrides) =>
        new()
        {
            SettingsOverride = static settings => settings with
            {
                Features = settings.Features with
                {
                    Embeddings = true,
                    Saga = true,
                    MemoryManagement = true,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = TestDimensions,
                    },
                },
            },
            ServiceOverrides = overrides,
        };

    /// <summary>Writes one Saga memory through the store's own insert.</summary>
    private static async Task<Guid> InsertSagaAsync(ArcanumWebApplicationFactory factory)
    {
        Guid id = Guid.NewGuid();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        SagaMemoryWriteOutcome outcome = await scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>().InsertAsync(
            id.ToString("D"),
            $"The ward-stone {id:N} lies under the mill.",
            DateTimeOffset.UtcNow,
            sessionId: null,
            tags: null,
            source: "extraction",
            new float[TestDimensions],
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

        return id;
    }

    /// <summary>Labels one Saga memory through the host's own ledger.</summary>
    private static async Task LabelSagaAsync(ArcanumWebApplicationFactory factory, Guid memoryId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        Result<LabeledArtifactWriteReceipt> receipt = await scope.ServiceProvider
            .GetRequiredService<IArtifactSensitivityLedger>()
            .LabelAsync(
                new DerivedArtifactWrite(
                    SensitiveArtifactKind.Saga,
                    memoryId,
                    null,
                    null,
                    null,
                    1,
                    CovenantOperationGateFixture.Digest(11),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.Parse("5E6F7081-92A3-4B5C-8D9E-0F1A2B3C4D5E")])),
                CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.IsFailure ? receipt.Error.Message : null);
    }

    /// <summary>Creates one Session through the route.</summary>
    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        string payload = JsonSerializer.Serialize(
            new CreateSessionRequest(CampaignId: null, Title: "an Entry under an unreadable label"),
            ArcanumJsonContext.Default.CreateSessionRequest);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/sessions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        return Assert.IsType<SessionDetailDto>(body?.Data).Id;
    }

    /// <summary>Appends one assistant Entry through the route and returns its identity.</summary>
    private static async Task<Guid> AppendAssistantEntryAsync(HttpClient client, Guid sessionId)
    {
        string payload = JsonSerializer.Serialize(
            new AppendEntryRequest(MessageRole.Assistant, "the reply that is deleted", "test-model"),
            ArcanumJsonContext.Default.AppendEntryRequest);

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/entries",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<EntryDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseEntryDto);

        return Assert.IsType<EntryDto>(body?.Data).Id;
    }

    private static async Task<ApiResponse<T>> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        string payload = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize(payload, typeInfo)
            ?? throw new InvalidOperationException($"Unreadable response: {payload}");
    }

    /// <summary>
    /// Counts the rows naming one identity, in any spelling, on a fresh read-only connection.
    /// Assertion-only.
    /// </summary>
    private static async Task<long> CountAsync(
        ArcanumWebApplicationFactory factory,
        string table,
        string column,
        Guid id)
    {
        Result<IGrimoireOrdinaryConnectionLease> opened = await factory.Services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, CancellationToken.None);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        await using SqliteCommand command = lease.Connection.CreateCommand();

        command.CommandText = $"SELECT count(*) FROM \"{table}\" WHERE lower(replace({column}, '-', '')) = $id;";

        _ = command.Parameters.AddWithValue("$id", id.ToString("N"));

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }
}
