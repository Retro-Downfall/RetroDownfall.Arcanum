using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// Spec §19.2 #5: a released memory can come back, and coming back is an ordinary memory that can be
/// erased again.
/// </summary>
/// <remarks>
/// Every step is the production one: the Session's entries arrive through the append route, the model
/// answers through the host's intelligence provider, extraction runs through its own service under its
/// own work lease, and the erase and the release go through their routes. Only the embedding provider is
/// a recording fake, because what it was asked is part of what is under test.
/// </remarks>
[Collection("ApiHost")]
public sealed class MemoryErasureReleaseEndToEndTests
{
    private const string T = "The ward-stone lies under the mill.";

    [SkippableFact]
    public async Task Released_content_is_extracted_again_and_a_second_erase_succeeds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingWeave weave = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            weave: weave);

        HttpClient client = factory.CreateAuthenticatedClient();

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        Guid session = await BoundSessionAsync(factory, client);

        // Pass 1 writes the memory.
        await AppendAsync(client, session, "Where did the old miller hide the ward-stone?");

        factory.FakeIntelligence.NextText = Conclusions(T);

        long firstThrough = await LatestSequenceAsync(factory, session);

        Assert.Equal(SagaExtractionOutcome.Completed, await ExtractAsync(factory, session, 0, firstThrough));

        string first = Assert.Single(await IdsWithContentAsync(factory, T));

        SagaMemoryDto erasedMemory;

        using (HttpResponseMessage shown = await client.GetAsync($"/api/memory/saga/{first}"))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

            erasedMemory = (await MemoryErasureRouteDriver.ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail)).Memory;
        }

        Assert.Equal(SagaMemoryScopeKind.Campaign, erasedMemory.ScopeKind);

        _ = await driver.EraseSagaAsync(first);

        // Pass 2: the erased content is withheld before it is embedded.
        await AppendAsync(client, session, "Is the stone still there?");

        factory.FakeIntelligence.NextText = Conclusions(T);

        long secondThrough = await LatestSequenceAsync(factory, session);

        Assert.Equal(SagaExtractionOutcome.Completed, await ExtractAsync(factory, session, firstThrough, secondThrough));

        Assert.Empty(await IdsWithContentAsync(factory, T));

        Assert.Equal(1, weave.Count(T));

        // Release, in the erased memory's own scope.
        MemoryErasureReleaseResultDto released = await driver.ReleaseSagaAsync(
            new(erasedMemory.ScopeKind, erasedMemory.ScopeCampaignId, T));

        Assert.Equal(new MemoryErasureReleaseResultDto(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.Released, 1), released);

        // Pass 3: the content comes back as a new memory.
        await AppendAsync(client, session, "Tell me about the mill again.");

        factory.FakeIntelligence.NextText = Conclusions(T);

        long thirdThrough = await LatestSequenceAsync(factory, session);

        Assert.Equal(SagaExtractionOutcome.Completed, await ExtractAsync(factory, session, secondThrough, thirdThrough));

        string second = Assert.Single(await IdsWithContentAsync(factory, T));

        Assert.NotEqual(first, second);

        Assert.Equal(2, weave.Count(T));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            SagaExtractionCursor? cursor = await scope.ServiceProvider
                .GetRequiredService<ISagaMemoryStore>()
                .GetExtractionCursorAsync(session, CancellationToken.None);

            Assert.NotNull(cursor);

            Assert.Equal(thirdThrough, cursor!.EntrySequence);
        }

        // And the memory that came back is erased again like any other.
        MemoryErasureRoundTrip<SagaEraseRequest> again = await driver.EraseSagaAsync(second);

        Assert.False(again.Result.Replayed);

        Assert.True(again.Result.Local.SuppressionFingerprintRecorded);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        // A release keeps the receipts it does not own.
        Assert.Equal(2, await ScalarAsync(factory, "SELECT count(*) FROM memory_erasure_receipts;"));

        using IServiceScope check = factory.Services.CreateScope();

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(await OpenAsync(check));
    }

    /// <summary>One extraction pass over the Session's entries, under the extraction work lease.</summary>
    private static async Task<SagaExtractionOutcome> ExtractAsync(
        ArcanumWebApplicationFactory factory,
        Guid session,
        long afterExclusive,
        long through)
    {
        SagaExtractionService extraction = factory.Services.GetRequiredService<SagaExtractionService>();

        IGrimoireConnectionAdmissionGate gate = factory.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease lease = acquired!;

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumSettings settings = factory.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue;

        return await extraction.ExtractForSessionAsync(
            scope.ServiceProvider,
            lease,
            new SagaExtractionRequest(
                session,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: afterExclusive,
                ThroughEntrySequence: through),
            settings.ResolveEmbeddings(),
            settings,
            CancellationToken.None);
    }

    /// <summary>
    /// Registers a Campaign through its route and binds a new Session to it through the turn-begin
    /// store, as a turn would.
    /// </summary>
    private static async Task<Guid> BoundSessionAsync(ArcanumWebApplicationFactory factory, HttpClient client)
    {
        string path = Path.Combine(factory.TempHome, "release-extraction-campaign");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await client.PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest("Release extraction", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        CampaignDto campaign = await MemoryErasureRouteDriver.ReadDataAsync(
            registered,
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        using IServiceScope scope = factory.Services.CreateScope();

        Result<Guid> session = await scope.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>().CreateBoundSessionAsync(
            CanonicalCampaignContext.Create(
                SessionCampaignBinding.ForCampaign(campaign.Id),
                campaignAvailabilityGeneration: 1,
                pathIdentityPolicyVersion: 1,
                pathIdentityRevision: null,
                rootIdentityDigest: null),
            "Release extraction",
            CancellationToken.None);

        Assert.True(session.IsSuccess, session.IsFailure ? session.Error.Message : null);

        return session.Value;
    }

    private static async Task AppendAsync(HttpClient client, Guid session, string content)
    {
        using HttpResponseMessage appended = await client.PostAsync(
            $"/api/sessions/{session}/entries",
            JsonContent.Create(
                new AppendEntryRequest(MessageRole.User, content),
                ArcanumJsonContext.Default.AppendEntryRequest));

        Assert.Equal(HttpStatusCode.OK, appended.StatusCode);
    }

    private static async Task<long> LatestSequenceAsync(ArcanumWebApplicationFactory factory, Guid session)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        return await db.Entries
            .Where(entry => entry.SessionId == session)
            .MaxAsync(entry => (long?)entry.Sequence) ?? 0L;
    }

    /// <summary>The extraction model's answer: one memory per conclusion, none from an attachment.</summary>
    private static string Conclusions(params string[] contents)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writer.WriteStartArray("memories");

            foreach (string content in contents)
            {
                writer.WriteStartObject();

                writer.WriteString("content", content);

                writer.WriteNull("attachmentId");

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The ids of every memory holding exactly this text, in any scope. Assertion-only.</summary>
    private static async Task<IReadOnlyList<string>> IdsWithContentAsync(ArcanumWebApplicationFactory factory, string content)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenAsync(scope);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Id FROM saga_memories WHERE Content = $content ORDER BY Id;";

        _ = command.Parameters.AddWithValue("$content", content);

        List<string> ids = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<long> ScalarAsync(ArcanumWebApplicationFactory factory, string sql)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        await using SqliteCommand command = (await OpenAsync(scope)).CreateCommand();

        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<SqliteConnection> OpenAsync(IServiceScope scope)
    {
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync();
        }

        return connection;
    }

    /// <summary>Answers one fixed vector for every text and remembers each text it was asked to embed.</summary>
    private sealed class RecordingWeave : IWeaveService
    {
        private readonly ConcurrentQueue<string> _texts = new();

        public bool IsAvailable => true;

        /// <summary>How many times this exact text was sent to be embedded.</summary>
        internal int Count(string text) => _texts.Count(recorded => string.Equals(recorded, text, StringComparison.Ordinal));

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            _texts.Enqueue(text);

            return Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vector())));
        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            foreach (string text in texts)
            {
                _texts.Enqueue(text);
            }

            return Task.FromResult(Result<Embedding<float>[]>.Success([.. texts.Select(static _ => new Embedding<float>(Vector()))]));
        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([(text, 0)]));

        private static float[] Vector()
        {
            float[] vector = new float[MemoryErasureRouteDriver.Dimensions];

            vector[0] = 1f;

            return vector;
        }
    }
}
