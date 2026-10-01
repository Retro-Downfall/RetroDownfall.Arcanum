using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>What one erase through the routes sent and got back: the preflight, the apply, and its result.</summary>
internal sealed record MemoryErasureRoundTrip<TApply>(
    MemoryErasurePreflightDto Preflight,
    TApply Apply,
    MemoryErasureResultDto Result);

/// <summary>
/// The one shared driver every erasure route suite uses: a host composed for erasure, production writes
/// for its preconditions, and the show → prepare → apply round trip each store's CLI verb makes.
/// </summary>
/// <remarks>
/// <para>Every precondition goes through a production writer: Saga memories through the store's own
/// insert, Covenant entries through the set prepare and commit routes, and a fingerprint only through an
/// actual erase. Raw SQL here is assertion-only.</para>
///
/// <para>The host never reaches the real keychain: <see cref="Host"/> replaces the credential store with
/// the caller's in-memory one, and a caller that restarts the host passes the same instance to the next
/// one, so the erasure key survives the restart exactly as it would in the OS store.</para>
///
/// <para>The client is authenticated on the way in. A caller that hands over a bare
/// <c>CreateClient()</c> gets the test API key added, and an unauthenticated probe uses its own client
/// rather than this driver.</para>
/// </remarks>
internal sealed class MemoryErasureRouteDriver
{
    /// <summary>The embedding width every erasure host is configured for, which is the clamp's floor.</summary>
    internal const int Dimensions = 64;

    private readonly HttpClient _client;

    internal MemoryErasureRouteDriver(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!client.DefaultRequestHeaders.Contains(ArcanumApiHeaders.ApiKey))
        {
            client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);
        }

        _client = client;
    }

    /// <summary>
    /// A host with Saga and embeddings on, the Covenant as asked, and the caller's credential store in
    /// place of every other.
    /// </summary>
    /// <param name="profile">
    /// When given, the host runs on that profile, so a second host started on it after this one is
    /// disposed reuses the same Grimoire.
    /// </param>
    /// <param name="weave">The embedding service, or a fake answering one fixed vector for every text.</param>
    /// <param name="configure">Runs last on the settings, after every setting this method chose.</param>
    internal static ArcanumWebApplicationFactory Host(
        InMemoryOsCredentialStore credentials,
        RestartableArcanumProfileFixture? profile = null,
        bool covenant = false,
        IWeaveService? weave = null,
        Action<ArcanumSettings>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        IWeaveService embeddings = weave ?? new FixedWeave();

        ArcanumWebApplicationFactory factory = profile is null ? new() : new(profile);

        factory.SettingsOverride = settings =>
        {
            ArcanumSettings patched = settings with
            {
                Features = settings.Features with
                {
                    Embeddings = true,
                    Saga = true,
                    Covenant = covenant,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = Dimensions,
                    },
                },
            };

            configure?.Invoke(patched);

            return patched;
        };

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<IOsCredentialStore>();

            services.AddSingleton<IOsCredentialStore>(credentials);

            services.RemoveAll<IWeaveService>();

            services.AddSingleton(embeddings);
        };

        return factory;
    }

    internal static ArcanumWebApplicationFactory CreateFactory(InMemoryOsCredentialStore credentials) =>
        Host(credentials, covenant: true);

    /// <summary>
    /// Writes one Saga memory through the store's insert, with the identity spelling extraction uses,
    /// and requires that it landed.
    /// </summary>
    internal static async Task<string> InsertSagaAsync(
        ArcanumWebApplicationFactory factory,
        string content,
        Guid? sessionId = null,
        CancellationToken ct = default)
    {
        string id = Guid.NewGuid().ToString();

        SagaMemoryWriteOutcome outcome = await InsertAsync(factory, id, content, sessionId, ct);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

        return id;
    }

    /// <summary>The same insert, reporting what the chokepoint decided rather than requiring a write.</summary>
    internal static Task<SagaMemoryWriteOutcome> InsertSagaOutcomeAsync(
        ArcanumWebApplicationFactory factory,
        string content,
        Guid? sessionId = null,
        CancellationToken ct = default) =>
        InsertAsync(factory, Guid.NewGuid().ToString(), content, sessionId, ct);

    /// <summary>How many fingerprints one store holds. Assertion-only.</summary>
    internal static async Task<long> FingerprintCountAsync(
        ArcanumWebApplicationFactory factory,
        MemoryReviewStore store,
        CancellationToken ct = default)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenAsync(scope, ct);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM memory_erasure_fingerprints WHERE StoreCode = $store;";

        _ = command.Parameters.AddWithValue("$store", (int)store);

        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Holds a read snapshot on a connection of its own until disposed, so a write-ahead-log checkpoint
    /// taken meanwhile cannot truncate.
    /// </summary>
    internal static async Task<IAsyncDisposable> HoldReaderAsync(
        ArcanumWebApplicationFactory factory,
        CancellationToken ct = default)
    {
        Result<IGrimoireOrdinaryConnectionLease> opened = await factory.Services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        IGrimoireOrdinaryConnectionLease lease = opened.Value;

        try
        {
            await using SqliteCommand snapshot = lease.Connection.CreateCommand();

            snapshot.CommandText = "BEGIN; SELECT count(*) FROM grimoire_feature_schemas;";

            _ = await snapshot.ExecuteScalarAsync(ct);
        }
        catch
        {
            await lease.DisposeAsync();

            throw;
        }

        return new HeldReader(lease);
    }

    /// <summary>Shows one Saga memory, then prepares and applies its erase, requiring 200 at each step.</summary>
    internal async Task<MemoryErasureRoundTrip<SagaEraseRequest>> EraseSagaAsync(
        string memoryId,
        Guid? mutationId = null,
        CancellationToken ct = default)
    {
        using HttpResponseMessage shown = await _client.GetAsync($"/api/memory/saga/{memoryId}", ct);

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        SagaMemoryDetail detail = await ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);

        SagaErasePrepareRequest prepare = new(
            memoryId,
            detail.ContentHash,
            detail.Claim?.CurrentVersionId,
            mutationId ?? Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareAsync(
            "/api/memory/saga/erase/prepare",
            prepare,
            ArcanumJsonContext.Default.SagaErasePrepareRequest,
            ct);

        SagaEraseRequest apply = new(
            prepare.MemoryId,
            prepare.ExpectedContentHash,
            prepare.ExpectedClaimVersionId,
            prepare.MutationId,
            preflight.PreflightToken);

        return new(preflight, apply, await ApplySagaAsync(apply, ct));
    }

    /// <summary>Shows one exact Lexicon entry, then prepares and applies its erase.</summary>
    internal async Task<MemoryErasureRoundTrip<LexiconEraseRequest>> EraseLexiconAsync(
        string name,
        Guid? campaignId,
        Guid? mutationId = null,
        CancellationToken ct = default)
    {
        LexiconCurationScope scope = new(
            campaignId is null ? LexiconScopeKind.Global : LexiconScopeKind.Campaign,
            campaignId);

        using HttpResponseMessage shown = await PostAsync(
            "/api/memory/lexicon/show",
            new LexiconShowRequest(name, scope),
            ArcanumJsonContext.Default.LexiconShowRequest,
            ct);

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        LexiconEntryDetail detail = await ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseLexiconEntryDetail);

        LexiconErasePrepareRequest prepare = new(detail.Target, mutationId ?? Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareAsync(
            "/api/memory/lexicon/erase/prepare",
            prepare,
            ArcanumJsonContext.Default.LexiconErasePrepareRequest,
            ct);

        LexiconEraseRequest apply = new(prepare.Target, prepare.MutationId, preflight.PreflightToken);

        return new(preflight, apply, await ApplyLexiconAsync(apply, ct));
    }

    /// <summary>Reads one scoped Covenant key's detail, then prepares and applies the erase of its entry.</summary>
    internal async Task<MemoryErasureRoundTrip<CovenantEraseRequest>> EraseCovenantAsync(
        CovenantScope scope,
        Guid? campaignId,
        string key,
        Guid? mutationId = null,
        CancellationToken ct = default)
    {
        using HttpResponseMessage shown = await PostAsync(
            "/api/memory/covenant/detail",
            new CovenantDetailRequest(scope, campaignId, key),
            ArcanumJsonContext.Default.CovenantDetailRequest,
            ct);

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        CovenantDetailDto detail = await ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseCovenantDetailDto);

        Assert.NotNull(detail.EntryId);

        CovenantErasePrepareRequest prepare = new(
            scope,
            campaignId,
            key,
            detail.EntryId!.Value,
            detail.Confirmed is { } confirmed ? new(confirmed.VersionId, confirmed.LaneRevision) : null,
            detail.Proposed is { } proposed ? new(proposed.VersionId, proposed.LaneRevision) : null,
            mutationId ?? Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareAsync(
            "/api/memory/covenant/erase/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantErasePrepareRequest,
            ct);

        CovenantEraseRequest apply = new(
            prepare.Scope,
            prepare.CampaignId,
            prepare.Key,
            prepare.EntryId,
            prepare.Confirmed,
            prepare.Proposed,
            prepare.MutationId,
            preflight.PreflightToken);

        return new(preflight, apply, await ApplyCovenantAsync(apply, ct));
    }

    internal Task<MemoryErasureResultDto> ApplySagaAsync(SagaEraseRequest request, CancellationToken ct = default) =>
        ApplyAsync("/api/memory/saga/erase", request, ArcanumJsonContext.Default.SagaEraseRequest, ct);

    internal Task<MemoryErasureResultDto> ApplyLexiconAsync(LexiconEraseRequest request, CancellationToken ct = default) =>
        ApplyAsync("/api/memory/lexicon/erase", request, ArcanumJsonContext.Default.LexiconEraseRequest, ct);

    internal Task<MemoryErasureResultDto> ApplyCovenantAsync(CovenantEraseRequest request, CancellationToken ct = default) =>
        ApplyAsync("/api/memory/covenant/erase", request, ArcanumJsonContext.Default.CovenantEraseRequest, ct);

    /// <summary>
    /// Sets one scoped Covenant key as the operator, through the set prepare and commit routes the CLI
    /// uses, at whatever revision the key's Confirmed lane holds now.
    /// </summary>
    internal async Task<CovenantMutationResultDto> SetCovenantAsync(
        CovenantScope scope,
        Guid? campaignId,
        string key,
        string content,
        CancellationToken ct = default)
    {
        Guid mutationId = Guid.NewGuid();

        CovenantSetPrepareRequest prepare = new(scope, campaignId, key, content, 0, mutationId, Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(prepare, ct);

        // A key that already holds a Confirmed head is set at that head's revision, which the preview
        // reports beside the one the request expected.
        if (preflight.CurrentLaneRevision != preflight.ExpectedLaneRevision)
        {
            prepare = prepare with { ExpectedRevision = preflight.CurrentLaneRevision };

            preflight = await PrepareSetAsync(prepare, ct);
        }

        CovenantSetRequest commit = new(
            prepare.Scope,
            prepare.CampaignId,
            prepare.Key,
            prepare.Content,
            prepare.ExpectedRevision,
            prepare.MutationId,
            prepare.Reactivate,
            preflight.PreflightToken);

        using HttpRequestMessage message = new(HttpMethod.Put, "/api/memory/covenant")
        {
            Content = JsonContent.Create(commit, ArcanumJsonContext.Default.CovenantSetRequest),
        };

        using HttpResponseMessage committed = await _client.SendAsync(message, ct);

        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);

        return await ReadDataAsync(committed, ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);
    }

    /// <summary>Posts one typed body and hands back the raw response, for status, header and refusal assertions.</summary>
    internal Task<HttpResponseMessage> PostAsync<TRequest>(
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> info,
        CancellationToken ct = default) =>
        _client.PostAsync(path, JsonContent.Create(body, info), ct);

    /// <summary>A success envelope's data, requiring the envelope to report success.</summary>
    internal static async Task<T> ReadDataAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> info)
    {
        ApiResponse<T>? body = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), info);

        Assert.NotNull(body);

        Assert.True(body!.IsSuccess, body.Error?.Code);

        Assert.NotNull(body.Data);

        return body.Data!;
    }

    /// <summary>A refusal envelope's error code, whatever its data type.</summary>
    internal static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement root = document.RootElement;

        Assert.False(root.GetProperty("isSuccess").GetBoolean());

        return root.GetProperty("error").GetProperty("code").GetString()!;
    }

    private async Task<MemoryErasurePreflightDto> PrepareAsync<TRequest>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> info,
        CancellationToken ct)
    {
        using HttpResponseMessage prepared = await PostAsync(path, request, info, ct);

        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        return await ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto);
    }

    private async Task<MemoryErasureResultDto> ApplyAsync<TRequest>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> info,
        CancellationToken ct)
    {
        using HttpResponseMessage applied = await PostAsync(path, request, info, ct);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        return await ReadDataAsync(applied, ArcanumJsonContext.Default.ApiResponseMemoryErasureResultDto);
    }

    private async Task<CovenantMutationPreflightDto> PrepareSetAsync(
        CovenantSetPrepareRequest prepare,
        CancellationToken ct)
    {
        using HttpResponseMessage prepared = await PostAsync(
            "/api/memory/covenant/set/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantSetPrepareRequest,
            ct);

        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        return await ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);
    }

    private static async Task<SagaMemoryWriteOutcome> InsertAsync(
        ArcanumWebApplicationFactory factory,
        string id,
        string content,
        Guid? sessionId,
        CancellationToken ct)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>().InsertAsync(
            id,
            content,
            DateTimeOffset.UtcNow,
            sessionId,
            tags: null,
            source: "extraction",
            Vector(),
            ct);
    }

    private static async Task<SqliteConnection> OpenAsync(IServiceScope scope, CancellationToken ct)
    {
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return connection;
    }

    private static float[] Vector()
    {
        float[] vector = new float[Dimensions];

        vector[0] = 1f;

        return vector;
    }

    /// <summary>Rolls the held snapshot back and releases its connection.</summary>
    private sealed class HeldReader(IGrimoireOrdinaryConnectionLease lease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using SqliteCommand rollback = lease.Connection.CreateCommand();

                rollback.CommandText = "ROLLBACK;";

                _ = await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                await lease.DisposeAsync();
            }
        }
    }

    /// <summary>Answers one fixed vector of the configured width for every text.</summary>
    private sealed class FixedWeave : IWeaveService
    {
        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vector())));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>[]>.Success([.. texts.Select(static _ => new Embedding<float>(Vector()))]));

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([(text, 0)]));
    }
}
