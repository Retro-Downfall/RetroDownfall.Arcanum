using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class DataRetentionEndpointTests
{
    [SkippableTheory]
    [InlineData("{}")]
    [InlineData("{\"request\":{\"operation\":\"Prune\"},\"expectedPlanId\":\"plan\",\"observedAt\":\"2026-10-28T00:00:00Z\",\"limit\":0}")]
    [InlineData("{\"request\":{\"operation\":\"Prune\"},\"expectedPlanId\":\"plan\",\"observedAt\":\"2026-10-28T00:00:00Z\",\"limit\":201}")]
    public async Task Prune_diagnostics_requires_a_bounded_explicit_snapshot_context(string body)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactory(new FakeDataRetentionService());

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/data/prune/diagnostics",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Prune_diagnostics_native_pages_reach_every_blocker_with_adjustable_limits()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid holder = Guid.NewGuid();

        Guid secondHolder = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();

            command.CommandText = """
                INSERT INTO Sessions(Id,Status,CreatedAt,UpdatedAt)
                VALUES(@holder,'archived','2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z'),
                    (@secondHolder,'archived','2000-01-01T00:00:00.0000000Z','2000-01-01T00:00:00.0000000Z');
                WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n < 33)
                INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence,IsPinned)
                SELECT printf('%08X-0000-0000-0000-%012X',n,n),@holder,1,'test','test',
                    '2000-01-01T00:00:00.0000000Z',n,1 FROM rows;
                WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n < 40)
                INSERT INTO InferenceRuns(Id,RequestId,SessionId,Surface,Purpose,StartedAt,Status)
                SELECT printf('%032x',n),printf('request-%d',n),@holder,'test','test',
                    '2000-01-01T00:00:00.0000000Z',0 FROM rows;
                """;

            System.Data.Common.DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = "@holder";

            parameter.Value = holder.ToString("D").ToUpperInvariant();

            command.Parameters.Add(parameter);

            System.Data.Common.DbParameter secondParameter = command.CreateParameter();

            secondParameter.ParameterName = "@secondHolder";

            secondParameter.Value = secondHolder.ToString("D").ToUpperInvariant();

            command.Parameters.Add(secondParameter);

            await command.ExecuteNonQueryAsync();
        }

        using HttpResponseMessage enabled = await client.PutAsync("/api/data/retention",
            new StringContent("{\"dataClass\":\"entries\",\"enabled\":true,\"days\":1}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        using HttpResponseMessage preview = await client.PostAsync("/api/data/prune/plan",
            new StringContent("{\"operation\":\"Prune\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        using JsonDocument planDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());

        JsonElement plan = planDocument.RootElement.GetProperty("data");

        Assert.Equal(73, plan.GetProperty("diagnostics").GetProperty("blockerCount").GetInt64());

        Assert.Equal(40, plan.GetProperty("diagnostics").GetProperty("conflictCount").GetInt64());

        Assert.Equal(20, plan.GetProperty("diagnostics").GetProperty("omittedConflictCount").GetInt64());

        string planId = plan.GetProperty("planId").GetString()!;

        string snapshotId = plan.GetProperty("diagnostics").GetProperty("snapshotId").GetString()!;

        string observedAt = plan.GetProperty("generatedAt").GetString()!;

        List<string> resources = [];

        string? continuation = null;

        string? firstContinuation = null;

        do
        {
            int limit = resources.Count == 0 ? 7 : 11;

            string body = $$"""
                {"request":{"operation":"Prune"},"expectedPlanId":"{{planId}}","expectedSnapshotId":"{{snapshotId}}","observedAt":"{{observedAt}}",
                 "limit":{{limit}},"continuation":{{(continuation is null ? "null" : "\"" + continuation + "\"")}}}
                """;

            using HttpResponseMessage response = await client.PostAsync("/api/data/prune/diagnostics",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using JsonDocument pageDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            JsonElement page = pageDocument.RootElement.GetProperty("data");

            Assert.Equal(113, page.GetProperty("totalCount").GetInt64());

            JsonElement details = page.GetProperty("details");

            Assert.InRange(details.GetArrayLength(), 1, limit);

            foreach (JsonElement detail in details.EnumerateArray())
            {
                Assert.Equal(holder.ToString("D"), detail.GetProperty("holdingSessionId").GetString());

                resources.Add(detail.GetProperty("isBlocker").GetBoolean() + ":" + detail.GetProperty("resourceId").GetString());
            }

            continuation = page.TryGetProperty("nextContinuation", out JsonElement next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;

            firstContinuation ??= continuation;
        }

        while (continuation is not null);

        Assert.Equal(113, resources.Count);

        Assert.Equal(113, resources.Distinct(StringComparer.Ordinal).Count());

        Assert.NotNull(firstContinuation);

        string decoded = Encoding.ASCII.GetString(Convert.FromBase64String(firstContinuation.Replace('-', '+').Replace('_', '/')
            .PadRight((firstContinuation.Length + 3) / 4 * 4, '=')));

        Assert.DoesNotContain(holder.ToString("D"), decoded, StringComparison.OrdinalIgnoreCase);

        foreach (string malformed in new[] { "not-a-cursor", firstContinuation + "=" })
        {
            DataRetentionPruneDiagnosticsRequest invalid = new(new(DataRetentionOperation.Prune), planId,
                snapshotId, DateTimeOffset.Parse(observedAt), malformed);

            using HttpResponseMessage refused = await client.PostAsync("/api/data/prune/diagnostics", JsonContent(
                invalid, ArcanumJsonContext.Default.DataRetentionPruneDiagnosticsRequest));

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        DataRetentionPruneDiagnosticsRequest changedFirstPage = new(
            new(DataRetentionOperation.Prune, Workspace: new(Guid.NewGuid(), "/private-unrelated-workspace")),
            planId, snapshotId, DateTimeOffset.Parse(observedAt));

        using HttpResponseMessage firstPageContext = await client.PostAsync("/api/data/prune/diagnostics", JsonContent(
            changedFirstPage, ArcanumJsonContext.Default.DataRetentionPruneDiagnosticsRequest));

        Assert.Equal(HttpStatusCode.Conflict, firstPageContext.StatusCode);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();

            command.CommandText = $"UPDATE Entries SET SessionId='{secondHolder.ToString("D").ToUpperInvariant()}' WHERE Id='00000021-0000-0000-0000-000000000021'";

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        string heldSnapshotBody = $$"""
            {"request":{"operation":"Prune"},"expectedPlanId":"{{planId}}","expectedSnapshotId":"{{snapshotId}}","observedAt":"{{observedAt}}",
             "limit":11}
            """;

        using HttpResponseMessage movedHolder = await client.PostAsync("/api/data/prune/diagnostics",
            new StringContent(heldSnapshotBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, movedHolder.StatusCode);

        DataRetentionPruneDiagnosticsRequest changedContext = new(
            new(DataRetentionOperation.Prune, Workspace: new(Guid.NewGuid(), "/private-unrelated-workspace")),
            planId, snapshotId, DateTimeOffset.Parse(observedAt), firstContinuation, 11);

        using HttpResponseMessage mismatched = await client.PostAsync("/api/data/prune/diagnostics", JsonContent(
            changedContext, ArcanumJsonContext.Default.DataRetentionPruneDiagnosticsRequest));

        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();

            command.CommandText = "UPDATE Entries SET Id='FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF' WHERE Id='00000021-0000-0000-0000-000000000021'";

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        DataRetentionPruneDiagnosticsRequest oldSnapshot = new(new(DataRetentionOperation.Prune), planId, snapshotId,
            DateTimeOffset.Parse(observedAt), firstContinuation, 11);

        using HttpResponseMessage stale = await client.PostAsync("/api/data/prune/diagnostics", JsonContent(
            oldSnapshot, ArcanumJsonContext.Default.DataRetentionPruneDiagnosticsRequest));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using HttpResponseMessage refreshed = await client.PostAsync("/api/data/prune/plan",
            new StringContent("{\"operation\":\"Prune\"}", Encoding.UTF8, "application/json"));

        using JsonDocument refreshedDocument = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());

        JsonElement refreshedPlan = refreshedDocument.RootElement.GetProperty("data");

        Assert.Equal(73, refreshedPlan.GetProperty("diagnostics").GetProperty("blockerCount").GetInt64());

        Assert.Equal(plan.GetProperty("blockers").GetRawText(), refreshedPlan.GetProperty("blockers").GetRawText());

        Assert.NotEqual(planId, refreshedPlan.GetProperty("planId").GetString());
    }
}
