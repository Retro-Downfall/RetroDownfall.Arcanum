using System.Net;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class DataRetentionEndpointTests
{
    [SkippableTheory]
    [InlineData(IdempotencyClaimState.Claimed)]
    [InlineData(IdempotencyClaimState.Running)]
    public async Task Rv242_native_plan_route_reports_expired_lease_until_actual_aged_cleanup(IdempotencyClaimState state)
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage rule = await client.PutAsync("/api/data/retention",
            JsonContent(new RetentionRuleUpdateRequest("idempotency-claims", true, 1), ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, rule.StatusCode);

        Guid id = await RetentionClaimRouteFixture.SeedAsync(factory, state);

        foreach (bool afterCleanup in new[] { false, true })
        {
            if (afterCleanup) Assert.Equal(1, await RetentionClaimRouteFixture.CleanupExpiredAsync(factory));

            using HttpResponseMessage response = await client.PostAsync("/api/data/prune/plan",
                JsonContent(new DataRetentionRequest(DataRetentionOperation.Prune), ArcanumJsonContext.Default.DataRetentionRequest));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            ApiResponse<DataRetentionPlan> envelope = await ReadAsync(response, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

            Assert.True(envelope.IsSuccess);

            DataRetentionPlan plan = Assert.IsType<DataRetentionPlan>(envelope.Data);

            if (afterCleanup)
            {
                Assert.DoesNotContain(plan.Blockers, blocker => blocker.ResourceId == id.ToString("D"));

                Assert.DoesNotContain(plan.Conflicts, conflict => conflict.ResourceId == id.ToString("D"));
            }
            else
            {
                DataRetentionBlocker blocker = Assert.Single(plan.Blockers, blocker => blocker.ResourceId == id.ToString("D"));

                Assert.Equal("Data.IdempotencyLeaseExpired", blocker.ReasonCode);

                Assert.Contains("24 hours", blocker.Message, StringComparison.Ordinal);

                Assert.DoesNotContain("reconciliation", blocker.Message, StringComparison.OrdinalIgnoreCase);

                Assert.DoesNotContain(plan.CandidateIds, candidate => candidate.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
