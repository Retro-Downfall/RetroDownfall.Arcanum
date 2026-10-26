using System.Net;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class MemoryEndpointTests
{
    [SkippableFact]
    public async Task Status_keeps_schema_progress_readable_when_newer_store_tables_do_not_exist_yet()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await ExecuteAsync((SqliteConnection)db.Database.GetDbConnection(),
                """
                INSERT INTO grimoire_schema_transitions (
                    FamilyCode, TransactionTierCode, FromVersion, TargetVersion, CompletedThroughVersion,
                    TargetSourceDefinitionFingerprint, BackfillName, BackfillCursor, BackfillRowsProcessed,
                    Revision, LastDurableErrorCode, StartedAtUtc, UpdatedAtUtc)
                VALUES (0,0,8,25,8,$fingerprint,'core-utc-instant-canonicalization',NULL,4096,16,NULL,$now,$now);
                DROP TABLE campaign_rollup_state;
                """, ("$fingerprint", new string('F', 64)),
                ("$now", "2026-10-26T00:00:00.0000000Z"));
        }

        using HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Empty(data.GetProperty("stores").EnumerateArray());

        Assert.False(data.GetProperty("storeCensusAvailable").GetBoolean());

        Assert.Single(data.GetProperty("schemaTransitions").GetProperty("runs").EnumerateArray());
    }

    [SkippableFact]
    public async Task Status_exposes_pending_schema_progress_without_private_cursors_or_error_text()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await ExecuteAsync((SqliteConnection)db.Database.GetDbConnection(),
                """
                INSERT INTO grimoire_schema_transitions (
                    FamilyCode, TransactionTierCode, FromVersion, TargetVersion, CompletedThroughVersion,
                    TargetSourceDefinitionFingerprint, BackfillName, BackfillCursor, BackfillRowsProcessed,
                    Revision, LastDurableErrorCode, StartedAtUtc, UpdatedAtUtc)
                VALUES (0, 0, 8, 25, 8, $fingerprint, 'core-utc-instant-canonicalization',
                    'PRIVATE-CURSOR-CANARY', 4096, 16, 'PRIVATE-ERROR-CANARY', $now, $now);
                """, ("$fingerprint", new string('F', 64)),
                ("$now", UtcInstantText.Format(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero))));
        }

        using HttpResponseMessage response = await client.GetAsync("/api/memory/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.True(data.TryGetProperty("schemaTransitions", out JsonElement status));

        Assert.True(status.GetProperty("journalAvailable").GetBoolean());

        JsonElement run = Assert.Single(status.GetProperty("runs").EnumerateArray());

        Assert.Equal("Core", run.GetProperty("tier").GetString());

        Assert.Equal(4096, run.GetProperty("rowsProcessed").GetInt64());

        Assert.Equal(8, run.GetProperty("completedThroughVersion").GetInt32());

        Assert.Equal(25, run.GetProperty("targetVersion").GetInt32());

        Assert.Equal("core-utc-instant-canonicalization", run.GetProperty("sweep").GetString());

        Assert.DoesNotContain("PRIVATE-", json, StringComparison.Ordinal);
    }
}
