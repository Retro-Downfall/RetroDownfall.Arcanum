using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(GuildWorkspaceLeaseState.Held)]
    [InlineData(GuildWorkspaceLeaseState.Draining)]
    [InlineData(GuildWorkspaceLeaseState.ReconciliationRequired)]
    [InlineData(GuildWorkspaceLeaseState.Released)]
    public async Task Cancellation_convergence_rechecks_descendant_physical_ownership_after_restart(GuildWorkspaceLeaseState state)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored parent = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(parent);

        GuildQuestStored child = await f.ChildAsync(parent, driver);

        GuildQuestDriver childDriver = await f.DriverAsync(child, "physical-child");

        var childResult = await f.Store.CommitResultAsync(childDriver, new(Guid.NewGuid(), ReceiptDigest("physical-child")),
            child.Quest.Revision, ReceiptResult(child.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true }, default);

        Assert.True(childResult.IsSuccess, childResult.Error.Message);

        var parentResult = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("physical-parent")),
            parent.Quest.Revision, ReceiptResult(parent.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true }, default);

        Assert.True(parentResult.IsSuccess, parentResult.Error.Message);

        await f.ExecuteAsync("""
            UPDATE guild_quests SET CancellationRequested=1,CancellationConverged=1,Revision=Revision+1;
            UPDATE guild_result_outbox SET DeliveryStateCode=3,Revision=Revision+1;
            """);

        await SeedCancellationLeaseAsync(f, child, state);

        f.Restart();

        var read = await f.Store.GetAsync(f.Caller, parent.Quest.Id, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(QuestState.Completed, read.Value.Quest.State);

        Assert.True(read.Value.Quest.CancellationRequested);

        Assert.Equal(state == GuildWorkspaceLeaseState.Released, read.Value.Quest.CancellationConverged);

        Assert.Equal(GuildAccountingState.Settled, read.Value.Quest.AccountingState);

        Assert.Equal(1L, await f.CountAsync("guild_workspace_leases"));
    }

    [Fact]
    public async Task Stopped_cancellation_with_uncertain_child_accounting_retains_cleanup_evidence_after_replay()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored parent = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(parent);

        GuildQuestStored child = await f.ChildAsync(parent, driver);

        GuildQuestDriver childDriver = await f.DriverAsync(child, "unsettled-child");

        var childResult = await f.Store.CommitResultAsync(childDriver, new(Guid.NewGuid(), ReceiptDigest("unsettled-child")),
            child.Quest.Revision, ReceiptResult(child.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true }, default);

        Assert.True(childResult.IsSuccess, childResult.Error.Message);

        var parentResult = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("unsettled-parent")),
            parent.Quest.Revision, ReceiptResult(parent.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true }, default);

        Assert.True(parentResult.IsSuccess, parentResult.Error.Message);

        await f.ExecuteAsync($"""
            UPDATE guild_quests SET CancellationRequested=1,CancellationConverged=1,Revision=Revision+1;
            UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1 WHERE Id='{child.Quest.Id:D}' COLLATE NOCASE;
            UPDATE guild_result_outbox SET DeliveryStateCode=3,Revision=Revision+1;
            """);

        f.Restart();

        var read = await f.Store.GetAsync(f.Caller, parent.Quest.Id, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.True(read.Value.Quest.CancellationConverged);

        GuildCleanupOwner owner = new(2, parent.Quest.SessionId!.Value, null);

        GuildQuestCleanupParticipant cleanup = new(f.Connections, TimeProvider.System);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Assert.True((await cleanup.SealAsync(owner, connection, transaction, default)).IsSuccess);

            await transaction.CommitAsync();
        }

        var first = await cleanup.ReconcileAsync(owner, default);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, first.Error.Code);

        f.Restart();

        var repeated = await cleanup.ReconcileAsync(owner, default);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, repeated.Error.Code);

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            var deleted = await cleanup.DeleteAsync(owner, connection, transaction, default);

            Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, deleted.Error.Code);
        }

        Assert.Equal(2L, await f.CountAsync("guild_quests"));

        Assert.Equal(2L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(2L, await f.CountAsync("fixture_settlements"));

        Assert.Equal(1L, await ReceiptScalarAsync(f, "SELECT count(*) FROM guild_quests WHERE AccountingStateCode=2"));
    }

    private static async Task SeedCancellationLeaseAsync(Fixture f, GuildQuestStored quest, GuildWorkspaceLeaseState state)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord lease = GuildWorkspaceLeaseStoreTests.Row();

        lease = lease with
        {
            Owner = lease.Owner with
            {
                Kind = GuildWorkspaceLeaseOwnerKind.Quest,
                QuestId = quest.Quest.Id,
                RootQuestId = quest.Quest.RootQuestId,
                SessionId = quest.Quest.SessionId,
                CampaignId = quest.Quest.CampaignId,
                InstallationGeneration = quest.Issuance.InstallationGeneration
            }
        };

        var inserted = await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, lease, default);

        Assert.True(inserted.IsSuccess, inserted.Error.Message);

        if (state is GuildWorkspaceLeaseState.Draining or GuildWorkspaceLeaseState.ReconciliationRequired)
        {
            var admitted = await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction, lease, lease.HeartbeatAt, default);

            Assert.True(admitted.IsSuccess, admitted.Error.Message);

            lease = admitted.Value;
        }

        if (state is GuildWorkspaceLeaseState.Draining or GuildWorkspaceLeaseState.Released)
        {
            var drained = await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, lease, "cancelled", default);

            Assert.True(drained.IsSuccess, drained.Error.Message);
        }

        if (state == GuildWorkspaceLeaseState.ReconciliationRequired)
        {
            var uncertain = await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction, lease.Id,
                lease.Epoch, lease.HostInstanceId, true, default);

            Assert.True(uncertain.IsSuccess, uncertain.Error.Message);
        }

        await transaction.CommitAsync();
    }
}
