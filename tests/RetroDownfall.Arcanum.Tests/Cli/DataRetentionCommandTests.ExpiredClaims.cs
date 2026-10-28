using System.Net;
using System.Net.Http.Json;
using System.Text;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed partial class DataRetentionCommandTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rv242_cli_renders_actual_native_plan_diagnostic_and_its_cleanup_disappearance(bool json)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage rule = await client.PutAsync("/api/data/retention",
            JsonContent.Create(new RetentionRuleUpdateRequest("idempotency-claims", true, 1), ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, rule.StatusCode);

        _ = await RetentionClaimRouteFixture.SeedAsync(factory, IdempotencyClaimState.Running);

        foreach (bool afterCleanup in new[] { false, true })
        {
            if (afterCleanup) Assert.Equal(1, await RetentionClaimRouteFixture.CleanupExpiredAsync(factory));

            using HttpResponseMessage response = await client.PostAsync("/api/data/prune/plan",
                JsonContent.Create(new DataRetentionRequest(DataRetentionOperation.Prune), ArcanumJsonContext.Default.DataRetentionRequest));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string wire = await response.Content.ReadAsStringAsync();

            RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(wire, Encoding.UTF8, "application/json"),
            });

            var result = RunCommand(handler, ["data", "prune", "--dry-run", json ? "--json" : "--plain"]);

            Assert.Equal(0, result.ExitCode);

            Assert.Equal("/api/data/prune/plan", Assert.Single(handler.Requests).Path);

            if (afterCleanup) Assert.DoesNotContain("Data.IdempotencyLeaseExpired", result.Output, StringComparison.Ordinal);
            else
            {
                Assert.Contains("Data.IdempotencyLeaseExpired", result.Output, StringComparison.Ordinal);

                Assert.Contains("24 hours", result.Output, StringComparison.Ordinal);

                Assert.DoesNotContain("reconciliation", result.Output, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
