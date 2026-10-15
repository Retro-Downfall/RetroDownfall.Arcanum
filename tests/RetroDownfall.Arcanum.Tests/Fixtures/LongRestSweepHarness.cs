using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal sealed partial class LongRestSweepHarness(SagaStoreHarness saga) : IAsyncDisposable
{
    internal SagaStoreHarness Saga { get; } = saga;

    internal SqliteConnection Connection => (SqliteConnection)Saga.Connection;

    internal LongRestDiscoveryStore Store { get; } = new(saga.Context, TimeProvider.System);

    /// <summary>The rendered-request budget the next generation's recipe carries; the default is the 32 KiB ceiling.</summary>
    internal int ProviderInputBudget { get; set; } = LongRestDiscoveryLimits.ProviderInputUtf8Bytes;

    /// <summary>
    /// Chooses the 64-wide vector a fold's generated summary embeds to. By default every layer-1 summary is the second axis and every
    /// upper one the first, so all summaries of a layer look alike; a test that needs summaries to differ, or to resemble one
    /// another by a chosen amount, sets this.
    /// </summary>
    internal Func<LongRestHierarchyWork, float[]>? FoldVector { get; set; }

    internal static async Task<LongRestSweepHarness> CreateAsync() => new(await SagaStoreHarness.CreateAsync(annalsEnabled: true));

    /// <summary>Gives a seeded memory a raw vector of any width, replacing the shared default embedding.</summary>
    internal Task SetEmbeddingAsync(string memory, float[] vector) => ExecuteAsync("UPDATE saga_memory_embeddings SET Dim = $dim, Embedding = $vector WHERE MemoryId = $memory;",
        ("$dim", vector.Length), ("$vector", EmbeddingBlobCodec.Encode(vector)), ("$memory", memory));

    /// <summary>
    /// The hierarchy lane alone, read through the store's actual scan before it merges with the other lanes, so what its summary
    /// selection, chain exclusion and expansion produced can be compared exactly.
    /// </summary>
    internal async Task<ImmutableArray<LongRestCandidate>> HierarchyLaneAsync(LongRestDiscoveryGeneration generation, LongRestTarget anchor,
        LongRestMaintenanceCapability? capability = null)
    {
        await using SqliteTransaction transaction = Connection.BeginTransaction();

        var method = typeof(LongRestDiscoveryStore).GetMethod("HierarchyCandidatesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return await (Task<ImmutableArray<LongRestCandidate>>)method.Invoke(null, [Connection, transaction, generation, anchor, capability, CancellationToken.None])!;
    }

    /// <summary>Every memory a fold's children expand to, in child order.</summary>
    internal static string[] MemoriesOf(LongRestHierarchyWork work) => [.. work.Fold.Children.SelectMany(static child => child.ExpandedTargets).Select(static target => target.MemoryId)];

    internal Task SeedAsync(string id, string text, Guid? session = null) => Saga.Store.InsertAsync(id, text,
        new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), session, null, "extraction", Saga.Embedding(), CancellationToken.None);

    /// <summary>
    /// A memory no other memory resembles: its text shares no word with another's and its vector is its own, so a corpus of them has
    /// no exact, lexical or vector neighbour and a sweep over it finds no work to pay for.
    /// </summary>
    internal async Task SeedDistinctAsync(string id, int number, Guid? session = null)
    {
        await Saga.Store.InsertAsync(id,
            $"alpha{number.ToString(System.Globalization.CultureInfo.InvariantCulture)} beta{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), session, null, "extraction", DistinctVector(number), CancellationToken.None);
    }

    /// <summary>
    /// Gives every generated summary a vector of its own, so no summary resembles an anchor or another summary and the hierarchy lane,
    /// which the default fold vectors would otherwise make every sibling summary a neighbour of, finds nothing either.
    /// </summary>
    internal void UseDistinctSummaries() => FoldVector = work => DistinctVector(1_000_000 + (work.Fold.Layer * 10_000) + checked((int)work.Fold.Group));

    /// <summary>
    /// A vector of its own: SplitMix64 from the number, so two numbers that differ by one are as unrelated as any two, which the framework's
    /// seeded generator does not promise. In sixty-four dimensions the cosine of two such vectors sits near zero, far below the recall floor.
    /// </summary>
    internal float[] DistinctVector(int number) => LongRestClusters.Background(number);

    internal async Task<LongRestDiscoveryGeneration> StartAsync(Guid? campaign = null, CovenantDigest? partition = null, CovenantTurnLease? authority = null,
        Guid? operationId = null)
    {
        Guid operation = operationId ?? Guid.NewGuid();

        await ExecuteAsync("INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, '2026-10-10T00:00:00.0000000Z', 'sweep');", ("$id", operation.ToString("N")));

        long highwater = (long)(await ScalarAsync("SELECT COALESCE(max(InsertionSequence), 0) FROM long_rest_sources;"))!;

        CovenantDigest digest = new(new byte[32]);

        LongRestSweepScope scope = LongRestSweepScope.Create(new(campaign is null ? LongRestSweepScopeKind.Global : LongRestSweepScopeKind.Campaign, campaign), partition).Value;

        CanonicalCampaignContext canonical = campaign is null ? CanonicalCampaignContext.GlobalOnly
            : CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(campaign.Value), 1, 1, 1, digest);

        LongRestSweepFence fence = new(canonical, highwater, authority?.Snapshot.DatasetGeneration ?? Guid.NewGuid(), 1, authority?.Snapshot.RuntimeAuthorityGeneration ?? 1, authority?.Snapshot.CapabilityGeneration ?? 1, new(Guid.NewGuid(), 0, digest));

        LongRestRecipeIdentity recipe = new(1, 1, "provider", "summary", digest, "provider", "embedding", digest, 64) { ProviderInputUtf8Bytes = ProviderInputBudget };

        LongRestDiscoveryGeneration generation = Require(await Store.StartOrResumeAsync(operation, scope, fence, recipe, CancellationToken.None));

        if (authority is not null)
        {
            Capability = await CreateCapabilityAsync(generation, authority);
        }

        return generation;
    }

    internal async Task<LongRestDiscoveryGeneration> CaptureAsync(Guid? campaign = null, CovenantDigest? partition = null, CovenantTurnLease? authority = null,
        Guid? operationId = null)
    {
        LongRestDiscoveryGeneration generation = await StartAsync(campaign, partition, authority, operationId);

        LongRestSourcePage page;

        do
        {
            page = Require(await Store.CaptureNextSourcePageAsync(generation.GenerationId, generation.Revision, Capability, CancellationToken.None));

            generation = generation with { Revision = page.Revision, CaptureCursor = page.CommittedCaptureCursor, Phase = page.HasMore ? LongRestSweepPhase.Capture : LongRestSweepPhase.Hierarchy };
        } while (page.HasMore);

        return generation;
    }

    internal async Task<LongRestTarget> TargetAsync(string memory)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = "SELECT v.VersionId, v.ContentHash FROM annal_claims c JOIN annal_heads h ON h.ClaimId = c.ClaimId JOIN annal_versions v ON v.VersionId = h.CurrentVersionId WHERE c.SubjectId = $memory;";

        command.Parameters.AddWithValue("$memory", memory);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return new(memory, reader.GetString(0), Convert.ToHexString((byte[])reader[1]));
    }

    internal async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    internal async Task<object?> ScalarAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    internal static T Require<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");

        return result.Value;
    }

    internal int SummarySends { get; private set; }

    internal int EmbeddingSends { get; private set; }

    internal async Task<LongRestPaidEvidence> PayAsync(LongRestWorkIdentity work, LongRestProviderPayloadKind kind,
        CovenantDigest payload, ulong ordinal)
    {
        LongRestDispatchAttempt attempt = new(Guid.NewGuid(), work, kind, ordinal, 3, LongRestDispatchAttemptState.Accepted,
            payload, new CovenantDigest(new byte[32]), work.Scope.ProtectedPartitionDigest is null ? null : new CovenantDigest(Enumerable.Repeat((byte)ordinal, 32).ToArray()));

        LongRestDispatchCompletion completion = new(LongRestDispatchAttemptState.Accepted, new(10, 2, 12, 0, 0), 0.01m, null);

        string json = JsonSerializer.Serialize(completion, LongRestJsonContext.Default.LongRestDispatchCompletion);

        Exception? fault = await Record.ExceptionAsync(() => ExecuteAsync("""
            INSERT INTO long_rest_provider_effects (WorkItemId, WorkDigest, OriginalWorkJson, AttemptId, PayloadKindCode, PhysicalOrdinal, Revision, StateCode,
                PayloadDigest, DestinationDigest, DisclosureReceiptDigest, SentCount, PaidCount, InputTokens, OutputTokens, TotalTokens, CostUsd, CompletionJson, UpdatedAtUtc)
            SELECT WorkItemId, WorkDigest, $original, $attempt, $kind, $ordinal, 3, 4, $payload, $destination, $receipt, 1, 1, 10, 2, 12, '0.01', $completion, '2026-10-10T00:00:00.0000000Z'
            FROM long_rest_work_items WHERE WorkDigest = $work
            ON CONFLICT(WorkItemId) DO UPDATE SET WorkDigest = excluded.WorkDigest, OriginalWorkJson = excluded.OriginalWorkJson, AttemptId = excluded.AttemptId,
                PayloadKindCode = excluded.PayloadKindCode, PhysicalOrdinal = excluded.PhysicalOrdinal, Revision = 3, StateCode = 4,
                PayloadDigest = excluded.PayloadDigest, DestinationDigest = excluded.DestinationDigest, DisclosureReceiptDigest = excluded.DisclosureReceiptDigest,
                SentCount = SentCount + 1, PaidCount = PaidCount + 1, InputTokens = InputTokens + 10, OutputTokens = OutputTokens + 2, TotalTokens = TotalTokens + 12,
                CostUsd = '0.02', CompletionJson = excluded.CompletionJson;
            """, ("$original", LongRestAttemptStore.EncodeOriginalWork(work)), ("$attempt", attempt.AttemptId.ToString("D").ToUpperInvariant()), ("$kind", (int)kind), ("$ordinal", checked((long)ordinal)),
            ("$payload", payload.Bytes), ("$destination", attempt.DestinationDigest.Bytes), ("$receipt", attempt.DisclosureReceiptDigest?.Bytes),
            ("$completion", json), ("$work", LongRestDiscoveryHash.Work(work).Value.Bytes)));

        Assert.Null(fault);

        if (kind == LongRestProviderPayloadKind.Summary)
        {
            SummarySends++;
        }
        else
        {
            EmbeddingSends++;
        }

        return new(attempt, 10, 2, 0.01m);
    }

    internal async Task<LongRestAcceptedHierarchySummary> SummaryAsync(LongRestHierarchyWork work, Guid? nodeId = null)
    {
        if (work.AcceptedSummary is not null)
        {
            return work.AcceptedSummary;
        }

        CovenantDigest payload = new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(work.Fold.Work.ItemId)));

        LongRestPaidEvidence paid = await PayAsync(work.Fold.Work, LongRestProviderPayloadKind.Summary, payload, 1);

        LongRestAcceptedHierarchySummary summary = new(work.Fold.Work.GenerationId, nodeId ?? Guid.NewGuid(), work.Fold, work.Fold.Work.RecipeDigest,
            payload, "tainttoken summary", new CovenantDigest(Convert.FromHexString("16BF55E33CE9EB3D1048B6939F5F5B64C42E28A511754DEA25F2114462170701")),
            work.Fold.Children[0].Sensitivity, work.Fold.Children[0].Provenance,
            [.. work.Fold.Children.SelectMany(child => child.LabelDigests).Distinct()], paid);

        return Require(await Store.CommitAcceptedHierarchySummaryAsync(summary, Capability, CancellationToken.None));
    }

    internal async Task<LongRestHierarchyPublication> EmbeddingAsync(LongRestHierarchyWork work, LongRestAcceptedHierarchySummary summary,
        string? overrideInput = null, ulong ordinal = 2)
    {
        CovenantDigest payload = LongRestEmbeddingPayloadHash.Create("provider", "embedding", 64, overrideInput ?? summary.Summary).Value;

        LongRestPaidEvidence paid = await PayAsync(work.Fold.Work, LongRestProviderPayloadKind.Embedding, payload, ordinal);

        float[] vector = FoldVector?.Invoke(work) ?? new float[64];

        if (FoldVector is null)
        {
            vector[work.Fold.Layer == 1 ? 1 : 0] = 1;
        }

        CovenantDigest value = new(SHA256.HashData(EmbeddingBlobCodec.Encode(vector)));

        return new(summary, new("provider", "embedding", 64, value), [.. vector], paid);
    }

    internal async Task<LongRestDiscoveryGeneration> BuildAsync(LongRestDiscoveryGeneration generation)
    {
        while (true)
        {
            LongRestHierarchyWork? work = Require(await Store.ReadNextFoldAsync(generation.GenerationId, Capability, CancellationToken.None));

            if (work is null)
            {
                long revision = (long)(await ScalarAsync($"SELECT Revision FROM long_rest_discovery_generations WHERE GenerationId = '{generation.GenerationId.ToString("D").ToUpperInvariant()}';"))!;

                return Require(await Store.PublishAsync(generation.GenerationId, revision, generation.Fence, Capability, CancellationToken.None));
            }

            LongRestAcceptedHierarchySummary summary = await SummaryAsync(work);

            LongRestHierarchyPublication publication = await EmbeddingAsync(work, summary);

            Require(await Store.CommitFoldAsync(publication, Capability, CancellationToken.None));
        }
    }

    public ValueTask DisposeAsync() => Saga.DisposeAsync();
}
