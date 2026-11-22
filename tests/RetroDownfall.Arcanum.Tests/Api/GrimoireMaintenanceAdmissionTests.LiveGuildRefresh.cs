using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("WorkspacePathPolicy")]
[Trait("Category", "Integration")]
public sealed class GrimoireMaintenanceLiveGuildRefreshTests
{
    [SkippableFact]
    public async Task Factory_reset_with_historical_guild_spend_evidence_reaches_commit()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using IServiceScope scope = harness.Factory.Services.CreateScope();

        string connectionString = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>().Database.GetConnectionString()!;

        using var selected = harness.Admission.SelectCurrentWorkExecution();

        await using GuildQuestStoreTests.LiveResetRefresh fixture = await GuildQuestStoreTests.LiveResetRefresh.StartAsync(
            connectionString, harness.SessionId, harness.Drain);

        Assert.Equal(ConnectionState.Open, fixture.OperationConnection.State);

        Assert.Equal(1, harness.Drain.RegisteredEnrolmentCountFor(fixture.OperationConnection));

        Assert.DoesNotContain(selected.Work, item => item.Kind == GrimoireWorkKind.GuildStorage && item.Acquired);

        var plan = await harness.PlanAsync(GrimoireTransitionEntryPoint.StandaloneFactoryReset);

        Assert.Empty(plan.Conflicts);

        using HttpRequestMessage request = harness.CreateApplyRequest(GrimoireTransitionEntryPoint.StandaloneFactoryReset, plan.PlanId, Guid.NewGuid());

        using CancellationTokenSource cancellation = new();

        Task<HttpResponseMessage> applying = harness.Client.SendAsync(request, cancellation.Token);

        try
        {
            await harness.Admission.StageOne.WaitUntilEnteredAsync();

            using HttpResponseMessage response = await applying.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.True(harness.Admission.StageOne.CompletedCount > 0);

            Assert.True(harness.Admission.StageTwo.CompletedCount > 0);

            Assert.Equal(ConnectionState.Closed, fixture.OperationConnection.State);
        }
        finally
        {
            cancellation.Cancel();

            try { using HttpResponseMessage response = await applying; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }
}
