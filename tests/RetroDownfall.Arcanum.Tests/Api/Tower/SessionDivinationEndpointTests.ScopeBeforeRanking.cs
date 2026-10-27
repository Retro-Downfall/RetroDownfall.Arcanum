using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

public sealed partial class SessionDivinationEndpointTests
{
    [SkippableTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Scope_and_status_apply_before_ranking_even_beyond_five_hundred_stronger_entries(
        bool differentCampaign, bool archivedDistractors)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        Guid campaign = await SeedCampaignAsync(factory);

        Guid otherCampaign = differentCampaign ? await SeedCampaignAsync(factory) : campaign;

        (Guid target, Guid targetEntry, _) = await SeedEmbeddedEntryAsync(
            factory, "Eligible target", [0.8f, 0.6f, 0f], campaign, "active");

        (Guid excluded, _, _) = await SeedEmbeddedEntryAsync(factory, "Stronger excluded entry", [1f, 0f, 0f],
            otherCampaign, archivedDistractors ? "archived" : "active");

        await AddStrongerEntriesAsync(factory, excluded, 600);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostDivineAsync(client,
            new SemanticSearchRequest("target", CampaignId: differentCampaign ? campaign : null, Status: " ACTIVE ", Limit: 1));

        SemanticSearchResult result = await ReadResultAsync(response);

        SemanticSessionSearchResult hit = Assert.Single(result.Results);

        Assert.Equal(target, hit.SessionId);

        Assert.Equal(targetEntry, hit.EntryId);

        Assert.InRange(hit.Similarity, 0.79f, 0.81f);

        Assert.False(result.HasMore);
    }

    [SkippableFact]
    public async Task Equal_similarity_prefers_the_newest_entry_before_the_identity_tie_breaker()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateEnabledFactory(new FakeWeaveService());

        (_, Guid first, _) = await SeedEmbeddedEntryAsync(factory, "First tied entry", [1f, 0f, 0f], null, "active");

        (_, Guid second, _) = await SeedEmbeddedEntryAsync(factory, "Second tied entry", [1f, 0f, 0f], null, "active");

        Guid newer = string.CompareOrdinal(first.ToString("D"), second.ToString("D")) > 0 ? first : second;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

            command.CommandText = """
                UPDATE Entries SET CreatedAt = CASE WHEN Id = @newer
                    THEN '2026-10-26T12:00:00.0000001Z'
                    ELSE '2026-10-26T12:00:00.0000000Z' END;
                """;

            command.Parameters.AddWithValue("@newer", newer.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync();
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostDivineAsync(client, new SemanticSearchRequest("tied", Limit: 1));

        SemanticSearchResult result = await ReadResultAsync(response);

        Assert.Equal(newer, Assert.Single(result.Results).EntryId);

        Assert.True(result.HasMore);
    }

    private static async Task AddStrongerEntriesAsync(ArcanumWebApplicationFactory factory, Guid session, int count)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM rows WHERE n < @count)
            INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
            SELECT printf('%08X-0000-4000-8000-000000000001',n), @session, 1, 'Excluded stronger entry', '',
                '2026-10-26T12:00:00.0000000Z', n+1 FROM rows;
            INSERT INTO entry_embeddings(EntryId,Embedding,Dim)
            SELECT Id,@vector,3 FROM Entries WHERE SessionId=@session AND Sequence>1;
            """;

        command.Parameters.AddWithValue("@count", count);

        command.Parameters.AddWithValue("@session", session.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("@vector", System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>([1f, 0f, 0f]).ToArray());

        _ = await command.ExecuteNonQueryAsync();
    }
}
