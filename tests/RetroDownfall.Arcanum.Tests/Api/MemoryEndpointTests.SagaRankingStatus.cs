using System.Net;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class MemoryEndpointTests
{
    [SkippableFact]
    public async Task Status_reports_full_clean_Saga_version_census_without_protected_classifications()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory(longRest: true);

        using IServiceScope scope = factory.Services.CreateScope();

        ISagaMemoryStore store = scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int index = 0; index < 52; index++)
        {
            await store.InsertAsync($"clean-{index:D2}", $"clean observation {index}", now.AddDays(-1), null, null, null,
                FixedVectorWeaveService.Vector(), CancellationToken.None);
        }

        await store.InsertAsync("dormant-clean", "OLD-CONTENT-CANARY", now.AddDays(-500), null, null, null,
            FixedVectorWeaveService.Vector(), CancellationToken.None);

        Guid protectedIdentity = Guid.NewGuid();

        await store.InsertAsync(protectedIdentity.ToString("N"), "PROTECTED-CONTENT-CANARY", now.AddDays(-500), null, null, null,
            FixedVectorWeaveService.Vector(), CancellationToken.None);

        IArtifactSensitivityLedger labels = scope.ServiceProvider.GetRequiredService<IArtifactSensitivityLedger>();

        Assert.True((await labels.LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.Saga, protectedIdentity,
            null, null, null, 1, DerivedArtifactContentDigest.ForText("PROTECTED-CONTENT-CANARY"), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()])), CancellationToken.None)).IsSuccess);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Equal(54, data.GetProperty("stores").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "Saga").GetProperty("count").GetInt32());

        Assert.True(data.TryGetProperty("sagaRanking", out JsonElement ranking), "Status must distinguish retained Saga rows from clean current-version ranking state.");

        Assert.True(ranking.GetProperty("cleanOnly").GetBoolean());

        Assert.True(ranking.GetProperty("enabled").GetBoolean());

        Assert.Equal(53, ranking.GetProperty("memoryCount").GetInt32());

        Assert.Equal(53, ranking.GetProperty("versionCount").GetInt32());

        Assert.Equal(1, ranking.GetProperty("dormantVersionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("positiveCreditVersionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("negativeCreditVersionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("uncorroboratedBenefitVersionCount").GetInt32());

        Assert.DoesNotContain("CONTENT-CANARY", json, StringComparison.Ordinal);

        Assert.DoesNotContain(protectedIdentity.ToString("N"), json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(protectedIdentity.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Status_omits_a_protected_current_version_even_without_a_Saga_label()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory(longRest: true);

        await SeedSagaMemoryAsync(factory, "clean", "ordinary clean observation", null);

        await SeedSagaMemoryAsync(factory, "protected-version-only", "initial clean observation", null);

        const string protectedContent = "HEAD-ONLY-PROTECTED-CANARY";

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

            await using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                await using SqliteCommand update = connection.CreateCommand();

                update.Transaction = transaction;

                update.CommandText = "UPDATE saga_memories SET Content = $content WHERE Id = 'protected-version-only'";

                update.Parameters.AddWithValue("$content", protectedContent);

                Assert.Equal(1, await update.ExecuteNonQueryAsync());

                DateTimeOffset now = DateTimeOffset.UtcNow;

                Assert.True(await AnnalsClaimWriter.AppendCorrectionAsync(connection, transaction, AnnalSubjectStore.Saga,
                    "protected-version-only", AnnalOrigin.AgentExtracted, SagaMemoryScopeKind.Global, null,
                    ContentSensitivity.CovenantDerived, AnnalContentDigest.ForSagaMemory(protectedContent),
                    now, now, null, CancellationToken.None));

                await transaction.CommitAsync();
            }

            await using SqliteCommand labels = connection.CreateCommand();

            labels.CommandText = "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 6";

            Assert.Equal(0L, await labels.ExecuteScalarAsync());
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement ranking = document.RootElement.GetProperty("data").GetProperty("sagaRanking");

        Assert.Equal(1, ranking.GetProperty("memoryCount").GetInt32());

        Assert.Equal(1, ranking.GetProperty("versionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("dormantVersionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("positiveCreditVersionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("negativeCreditVersionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("uncorroboratedBenefitVersionCount").GetInt32());

        Assert.DoesNotContain(protectedContent, json, StringComparison.Ordinal);

        Assert.DoesNotContain("protected-version-only", json, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Session_status_limits_Saga_census_to_the_exact_session_and_canonical_scope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory(campaignScopedMemory: true, longRest: true);

        Guid campaignA = Guid.NewGuid();

        Guid campaignB = Guid.NewGuid();

        Guid sessionA = await SeedCampaignSessionAsync(factory, campaignA);

        Guid siblingA = await SeedCampaignSessionAsync(factory, campaignA);

        Guid sessionB = await SeedCampaignSessionAsync(factory, campaignB);

        await SeedSagaMemoryAsync(factory, "selected", "selected session memory", sessionA);

        await SeedSagaMemoryAsync(factory, "mismatched-scope", "selected session without scope authority", sessionA);

        await SeedSagaMemoryAsync(factory, "sibling", "same Campaign other session", siblingA);

        await SeedSagaMemoryAsync(factory, "other-campaign", "other Campaign memory", sessionB);

        await SeedSagaMemoryAsync(factory, "global", "global memory", null);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await ExecuteAsync((SqliteConnection)db.Database.GetDbConnection(),
                "UPDATE saga_memories SET CampaignId = $campaign WHERE Id = 'mismatched-scope'",
                ("$campaign", campaignB.ToString("D").ToUpperInvariant()));
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync($"/api/memory/status/{sessionA:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Equal(2, data.GetProperty("stores").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "Saga").GetProperty("count").GetInt32());

        Assert.True(data.TryGetProperty("sagaRanking", out JsonElement ranking));

        Assert.Equal(1, ranking.GetProperty("memoryCount").GetInt32());

        Assert.Equal(1, ranking.GetProperty("versionCount").GetInt32());
    }

    [SkippableFact]
    public async Task Disabled_status_keeps_legacy_memories_neutral_without_inventing_versions()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory();

        await SeedSagaMemoryAsync(factory, "legacy-unversioned", "retained legacy content", null);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(document.RootElement.GetProperty("data").TryGetProperty("sagaRanking", out JsonElement ranking));

        Assert.False(ranking.GetProperty("enabled").GetBoolean());

        Assert.Equal(1, ranking.GetProperty("memoryCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("versionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("dormantVersionCount").GetInt32());

        Assert.Equal(0, ranking.GetProperty("positiveCreditVersionCount").GetInt32());
    }
}
