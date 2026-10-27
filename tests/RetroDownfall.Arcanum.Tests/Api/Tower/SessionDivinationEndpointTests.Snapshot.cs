using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

public sealed partial class SessionDivinationEndpointTests
{
    [SkippableFact]
    public async Task Caller_cancellation_interrupts_native_work_and_keeps_the_borrowed_connection_usable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FixtureOrdinaryConnectionFactory connections = new();

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService(), connections);

        _ = await SeedEmbeddedEntryAsync(factory, "cancel native scan", [1f, 0f, 0f], null, "active");

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.CreateFunction("native_scan_entered", () =>
        {
            entered.TrySetResult();

            return 1L;
        });

        await using (SqliteCommand setup = connection.CreateCommand())
        {
            // A deterministic native VM workload, entered before returning the first row. No wall-clock
            // sleep or provider call: the emergency interrupt below always contains the RED execution.
            setup.CommandText = """
                ALTER TABLE entry_embeddings RENAME TO stored_embeddings;
                CREATE VIEW entry_embeddings AS
                SELECT EntryId, CASE WHEN (
                    WITH RECURSIVE work(n) AS (SELECT native_scan_entered()
                        UNION ALL SELECT n+1 FROM work WHERE n<100000000)
                    SELECT sum(n) FROM work) = 0 THEN Embedding ELSE Embedding END AS Embedding
                FROM stored_embeddings;
                """;

            await setup.ExecuteNonQueryAsync();
        }

        using CancellationTokenSource cancellation = new();

        ISessionDivinationService service = scope.ServiceProvider.GetRequiredService<ISessionDivinationService>();

        Task<Result<SemanticSearchResult>> searching = Task.Run(() => service.SearchAsync(
            new Embedding<float>(new float[] { 1, 0, 0 }), null, null, 1, 0, cancellation.Token));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            cancellation.Cancel();

            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => searching.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_interrupt(connection.Handle);

            try
            {
                _ = await searching.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.Equal(0, connections.LiveLeaseCount);

        await using SqliteCommand healthy = connection.CreateCommand();

        healthy.CommandText = "SELECT 42;";

        Assert.Equal(42L, await healthy.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task Ranking_and_metadata_share_one_snapshot_and_score_only_eligible_entries_once()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FixtureOrdinaryConnectionFactory connections = new();

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService(), connections);

        Guid campaign = await SeedCampaignAsync(factory);

        Guid other = await SeedCampaignAsync(factory);

        (Guid session, Guid entry, string? title) = await SeedEmbeddedEntryAsync(
            factory, "Original snapshot content", [1f, 0f, 0f], campaign, "active");

        (Guid excluded, _, _) = await SeedEmbeddedEntryAsync(factory, "Outside scope", [1f, 0f, 0f], other, "active");

        await AddStrongerEntriesAsync(factory, excluded, 600);

        int rankPasses = 0;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable checkpoint = GrimoireScopedConsumerTestSeam.Override("SessionDivinationService.RankAsync", (observation, token) =>
        {
            Assert.Equal(1, observation.Observation);

            Assert.Equal(1, connections.LiveLeaseCount);

            Interlocked.Increment(ref rankPasses);

            entered.TrySetResult();

            return new ValueTask(resume.Task.WaitAsync(token));
        });

        using HttpClient client = factory.CreateAuthenticatedClient();

        Task<HttpResponseMessage> searching = PostDivineAsync(client, new SemanticSearchRequest("snapshot", CampaignId: campaign, Limit: 1));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using SqliteCommand writer = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

            writer.CommandText = """
                UPDATE Sessions SET Status='archived', CampaignId=@other, Title='Changed title' WHERE Id=@session;
                UPDATE Entries SET Content='Changed snapshot content',CreatedAt='2026-10-26T23:59:59.0000001Z' WHERE Id=@entry;
                UPDATE entry_embeddings SET Embedding=@vector WHERE EntryId=@entry;
                """;

            writer.Parameters.AddWithValue("@other", other.ToString("D").ToUpperInvariant());

            writer.Parameters.AddWithValue("@session", session.ToString("D").ToUpperInvariant());

            writer.Parameters.AddWithValue("@entry", entry.ToString("D").ToUpperInvariant());

            writer.Parameters.AddWithValue("@vector", EmbeddingBlobCodec.Encode(new float[] { 0, 1, 0 }));

            Assert.Equal(3, await writer.ExecuteNonQueryAsync());
        }
        finally
        {
            resume.TrySetResult();
        }

        using HttpResponseMessage response = await searching.WaitAsync(TimeSpan.FromSeconds(10));

        SemanticSearchResult result = await ReadResultAsync(response);

        SemanticSessionSearchResult hit = Assert.Single(result.Results);

        Assert.Equal(entry, hit.EntryId);

        Assert.Equal(title, hit.SessionTitle);

        Assert.Equal("Original snapshot content", hit.EntryContentPreview);

        Assert.Equal(1f, hit.Similarity);

        Assert.False(result.HasMore);

        Assert.Equal(1, rankPasses);

        Assert.Equal(0, connections.LiveLeaseCount);

        checkpoint.Dispose();

        using HttpResponseMessage later = await PostDivineAsync(client, new SemanticSearchRequest("snapshot", CampaignId: campaign, Limit: 1));

        SemanticSearchResult next = await ReadResultAsync(later);

        Assert.Empty(next.Results);

        Assert.False(next.HasMore);
    }

    [SkippableTheory]
    [InlineData("mismatch", false)]
    [InlineData("mismatch", true)]
    [InlineData("unaligned", false)]
    [InlineData("unaligned", true)]
    [InlineData("zero", false)]
    [InlineData("zero", true)]
    [InlineData("nan", false)]
    [InlineData("nan", true)]
    public async Task Uncomparable_vectors_are_skipped_without_inventing_similarity_or_more(string kind, bool healthyNeighbor)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        (_, Guid invalid, _) = await SeedEmbeddedEntryAsync(factory, "Uncomparable vector", [1f, 0f, 0f], null, "active");

        Guid? healthy = null;

        if (healthyNeighbor)
        {
            (_, Guid id, _) = await SeedEmbeddedEntryAsync(factory, "Healthy vector", [1f, 0f, 0f], null, "active");

            healthy = id;
        }

        byte[] blob = kind switch
        {
            "unaligned" => [1],
            "mismatch" => EmbeddingBlobCodec.Encode(new float[] { 1, 0 }),
            "zero" => EmbeddingBlobCodec.Encode(new float[] { 0, 0, 0 }),
            "nan" => EmbeddingBlobCodec.Encode(new float[] { float.NaN, 0, 0 }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

            command.CommandText = "UPDATE entry_embeddings SET Embedding=@blob WHERE EntryId=@id;";

            command.Parameters.AddWithValue("@blob", blob);

            command.Parameters.AddWithValue("@id", invalid.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostDivineAsync(client, new SemanticSearchRequest("vector", Limit: 1));

        if (kind == "mismatch" && !healthyNeighbor)
        {
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

            await AssertErrorCodeAsync(response, ErrorCodes.Embeddings.DimensionMismatch);

            return;
        }

        SemanticSearchResult result = await ReadResultAsync(response);

        if (healthy is { } expected)
        {
            Assert.Equal(expected, Assert.Single(result.Results).EntryId);
        }
        else
        {
            Assert.Empty(result.Results);
        }

        Assert.False(result.HasMore);
    }

    [SkippableFact]
    public async Task Equal_similarity_and_date_use_entry_identity_as_a_deterministic_cutoff()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        (_, Guid first, _) = await SeedEmbeddedEntryAsync(factory, "First", [1f, 0f, 0f], null, "active");

        (_, Guid second, _) = await SeedEmbeddedEntryAsync(factory, "Second", [1f, 0f, 0f], null, "active");

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

            command.CommandText = "UPDATE Entries SET CreatedAt='2026-10-26T12:00:00.0000000Z';";

            Assert.Equal(2, await command.ExecuteNonQueryAsync());
        }

        Guid expected = string.CompareOrdinal(first.ToString("D"), second.ToString("D")) < 0 ? first : second;

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostDivineAsync(client, new SemanticSearchRequest("tie", Limit: 1));

        SemanticSearchResult result = await ReadResultAsync(response);

        Assert.Equal(expected, Assert.Single(result.Results).EntryId);

        Assert.True(result.HasMore);
    }
}
