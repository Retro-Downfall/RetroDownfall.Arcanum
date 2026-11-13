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

public sealed partial class GrimoireMaintenanceAdmissionTests
{
    [SkippableFact]
    public async Task Factory_reset_waits_for_real_terminal_refresh_body_disposal_and_heartbeat()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using IServiceScope scope = harness.Factory.Services.CreateScope();

        string connectionString = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>().Database.GetConnectionString()!;

        using var selected = harness.Admission.SelectCurrentWorkExecution();

        await using GuildQuestStoreTests.LiveResetRefresh refresh = await GuildQuestStoreTests.LiveResetRefresh.StartAsync(
            connectionString, harness.SessionId, harness.Admission, harness.Drain);

        var refreshWork = Assert.Single(selected.Work,
            item => item.Kind == GrimoireWorkKind.GuildStorage && item.Acquired && item.Disposals == 0);

        var refreshEffect = Assert.Single(selected.Effects,
            item => item.Kind == GrimoireWorkKind.GuildStorage && item.Disposals == 0);

        Assert.Equal(ConnectionState.Open, refresh.OperationConnection.State);

        Assert.Equal(1, harness.Drain.RegisteredEnrolmentCountFor(refresh.OperationConnection));

        byte[] before = await RefreshSourceFingerprintAsync(connectionString);

        var plan = await harness.PlanAsync(GrimoireTransitionEntryPoint.StandaloneFactoryReset);

        Assert.Empty(plan.Conflicts);

        long initialGeneration = harness.Admission.CurrentGeneration;

        Guid requestedOperation = Guid.NewGuid();

        using HttpRequestMessage request = harness.CreateApplyRequest(GrimoireTransitionEntryPoint.StandaloneFactoryReset, plan.PlanId, requestedOperation);

        using CancellationTokenSource applyCancellation = new();

        Task<HttpResponseMessage> applying = harness.Client.SendAsync(request, applyCancellation.Token);

        try
        {
            await harness.Admission.StageOne.WaitUntilEnteredAsync();

            Assert.False(applying.IsCompleted);

            Assert.Equal(0, harness.Admission.StageOne.CompletedCount);

            Assert.Equal(0, refreshWork.Disposals);

            Assert.Equal(0, refreshEffect.Disposals);

            Assert.Equal(before, await RefreshSourceFingerprintAsync(connectionString));

            refresh.ReleaseRead();

            await refresh.DisposalStarted.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(applying.IsCompleted);

            Assert.Equal(before, await RefreshSourceFingerprintAsync(connectionString));

            refresh.ReleaseDisposal();

            await refresh.WaitForHeartbeatStopAsync();

            Assert.False(refresh.HeartbeatFinished);

            Assert.Equal(0, harness.Admission.StageOne.CompletedCount);

            Assert.Equal(0, refreshWork.Disposals);

            Assert.Equal(0, refreshEffect.Disposals);

            Assert.False(applying.IsCompleted);

            Assert.Equal(before, await RefreshSourceFingerprintAsync(connectionString));

            refresh.ReleaseHeartbeat();

            await refresh.JoinAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(1, refreshWork.Disposals);

            Assert.Equal(1, refreshEffect.Disposals);

            HttpResponseMessage response;

            try { response = await applying.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                string active = string.Join(",", harness.Admission.WorkAttempts.Where(item => item.Acquired && item.Disposals == 0).Select(item => item.Kind));

                Assert.Fail($"Reset timed out after refresh join: stage1={harness.Admission.StageOne.EnteredCount}/{harness.Admission.StageOne.CompletedCount}, stage2={harness.Admission.StageTwo.EnteredCount}/{harness.Admission.StageTwo.CompletedCount}, activeWork={active}");

                throw;
            }

            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var envelope = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

                Assert.NotNull(envelope);

                Assert.True(envelope.IsSuccess);

                Assert.NotNull(envelope.Data);

                Assert.True(envelope.Data.Reconciled);

                Assert.Empty(envelope.Data.Conflicts);

                Assert.Equal(requestedOperation, envelope.Data.RequestedOperationId);

                Assert.Equal(initialGeneration + 1, harness.Admission.CurrentGeneration);

                Assert.True(harness.Admission.StageTwo.CompletedCount > 0);

                Assert.Equal(ConnectionState.Closed, refresh.OperationConnection.State);

                using IServiceScope inspection = harness.Factory.Services.CreateScope();

                LongRunningOperation operation = Assert.IsType<LongRunningOperation>(await inspection.ServiceProvider
                    .GetRequiredService<ILongRunningOperationStore>().GetAsync(envelope.Data.OperationId));

                Assert.Equal(LongRunningOperationState.Completed, operation.State);

                var opened = await inspection.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                    .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

                Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

                await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

                await using SqliteCommand source = lease.Connection.CreateCommand();

                source.CommandText = "SELECT COUNT(*) FROM guild_spend_sources";

                Assert.Equal(0L, await source.ExecuteScalarAsync());
            }
        }
        finally
        {
            refresh.ReleaseRead();

            refresh.ReleaseDisposal();

            refresh.ReleaseHeartbeat();

            applyCancellation.Cancel();

            try { using HttpResponseMessage response = await applying; }
            catch (OperationCanceledException) when (applyCancellation.IsCancellationRequested) { }
        }
    }

    [SkippableFact]
    public async Task Factory_reset_no_refresh_control_with_same_capture_fixture_reaches_commit()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using IServiceScope scope = harness.Factory.Services.CreateScope();

        string connectionString = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>().Database.GetConnectionString()!;

        using var selected = harness.Admission.SelectCurrentWorkExecution();

        await using GuildQuestStoreTests.LiveResetRefresh fixture = await GuildQuestStoreTests.LiveResetRefresh.StartAsync(
            connectionString, harness.SessionId, harness.Admission, harness.Drain, startRefresh: false);

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

    private static async Task<byte[]> RefreshSourceFingerprintAsync(string connectionString)
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(connectionString, default);

        await using SqliteCommand command = connection.CreateCommand();

        using MemoryStream bytes = new();

        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);

        foreach (string query in new[] { "SELECT * FROM guild_spend_sources ORDER BY EffectId", "SELECT * FROM guild_spend_authority_state ORDER BY StateKey" })
        {
            command.CommandText = query;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            writer.Write(reader.FieldCount);

            for (int i = 0; i < reader.FieldCount; i++)
            {
                object value = reader.GetValue(i);

                switch (value)
                {
                    case DBNull:
                        writer.Write((byte)0);

                        break;
                    case long integer:
                        writer.Write((byte)1);

                        writer.Write(integer);

                        break;
                    case string text:
                        writer.Write((byte)2);

                        writer.Write(text);

                        break;
                    case byte[] blob:
                        writer.Write((byte)3);

                        writer.Write(blob.Length);

                        writer.Write(blob);

                        break;
                    default:
                        Assert.Fail("Unexpected source storage class.");

                        break;
                }
            }

            Assert.False(await reader.ReadAsync());
        }

        writer.Flush();

        return SHA256.HashData(bytes.ToArray());
    }
}
