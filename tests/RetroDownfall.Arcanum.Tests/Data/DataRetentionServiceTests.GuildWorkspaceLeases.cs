using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(GuildWorkspaceLeaseState.Held)]
    [InlineData(GuildWorkspaceLeaseState.Draining)]
    [InlineData(GuildWorkspaceLeaseState.ReconciliationRequired)]
    public async Task Factory_reset_refuses_live_draining_or_uncertain_workspace_ownership(GuildWorkspaceLeaseState state)
    {
        RequireSqlCipher();

        var seeded = await SeedWorkspaceLeaseAsync(state);

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired
            && conflict.ResourceId == seeded.Row.Id.ToString("D"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_workspace_leases"));
    }

    [SkippableFact]
    public async Task Quiescent_factory_reset_deletes_released_lease_evidence_and_mints_a_new_native_operator()
    {
        RequireSqlCipher();

        var seeded = await SeedWorkspaceLeaseAsync(GuildWorkspaceLeaseState.Released);

        var reset = await ReconcileFactoryResetV0Async(CreateService(), "guild-workspace-leases-reset");

        Assert.Equal(1, reset.Summary.Completed);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_workspace_leases"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_workspace_lease_mutations"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_workspace_authority_state"));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        var replacement = await GuildWorkspaceLeaseStore.GetNativeOperatorPrincipalWithinAsync(connection, transaction,
            seeded.Row.Owner.InstallationGeneration, default);

        Assert.True(replacement.IsSuccess, replacement.Error.Message);

        Assert.NotEqual(seeded.Row.Owner.PrincipalId, replacement.Value);
    }

    private async Task<(GuildWorkspaceLeaseRecord Row, Guid Principal)> SeedWorkspaceLeaseAsync(GuildWorkspaceLeaseState state)
    {
        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildWorkspaceLeaseRecord row = GuildWorkspaceLeaseStoreTests.Row();

        var principal = await GuildWorkspaceLeaseStore.GetNativeOperatorPrincipalWithinAsync(connection, transaction,
            row.Owner.InstallationGeneration, default);

        Assert.True(principal.IsSuccess, principal.Error.Message);

        row = row with { Owner = row.Owner with { PrincipalId = principal.Value } };

        Assert.True((await GuildWorkspaceLeaseStore.InsertWithinAsync(connection, transaction, row, default)).IsSuccess);

        if (state is GuildWorkspaceLeaseState.Draining or GuildWorkspaceLeaseState.ReconciliationRequired)
            row = (await GuildWorkspaceLeaseStore.AdmitPermitWithinAsync(connection, transaction, row, row.HeartbeatAt, default)).Value;

        if (state is GuildWorkspaceLeaseState.Draining or GuildWorkspaceLeaseState.Released)
        {
            row = (await GuildWorkspaceLeaseStore.DrainWithinAsync(connection, transaction, row, "released", default)).Value;

            var receipt = await GuildWorkspaceLeaseStore.RecordMutationWithinAsync(connection, transaction, row,
                new(Guid.NewGuid(), new string('B', 64)), GuildAuthorityAction.LeaseRelease, row.HeartbeatAt, default);

            Assert.True(receipt.IsSuccess, receipt.Error.Message);
        }

        if (state == GuildWorkspaceLeaseState.ReconciliationRequired)
            row = (await GuildWorkspaceLeaseStore.FinishPermitWithinAsync(connection, transaction, row.Id, row.Epoch,
                row.HostInstanceId, true, default)).Value;

        await transaction.CommitAsync();

        return (row, principal.Value);
    }
}
