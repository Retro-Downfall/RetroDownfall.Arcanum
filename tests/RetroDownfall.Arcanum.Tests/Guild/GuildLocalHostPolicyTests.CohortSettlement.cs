using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cohort_settlement_clock_cannot_be_deleted_while_its_live_accounts_remain()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        _ = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        await using var connection = await f.OpenAsync();

        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM guild_cohort_settlement_clock";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_settlement_clock"));
    }

    [Fact]
    public async Task Cohort_settlement_recomputes_completed_work_after_its_accounting_evidence_settles()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.First, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "stopped with uncertain accounting", recordBilling: false);

        var parent = await f.Store.GetAsync(owner, members[0].Quest.Id, default);

        Assert.True(parent.IsSuccess, parent.Error.Message);

        Assert.NotEqual(GuildAccountingState.Settled, parent.Value.Quest.AccountingState);

        var settled = await f.Authority.InQuestTransactionAsync<GuildBudgetEffect>(root.Quest.RootQuestId,
            async (connection, transaction, _, ct) =>
            {
                await using var command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = "SELECT EffectId FROM guild_budget_effects WHERE QuestId=$quest";

                command.Parameters.AddWithValue("$quest", members[0].Quest.Id.ToString("D").ToUpperInvariant());

                Guid effect = Guid.Parse((string)(await command.ExecuteScalarAsync(ct))!);

                return await new GuildBudgetLedger().ApplyEvidenceWithinAsync(connection, transaction, effect,
                    new(Guid.NewGuid(), new string('B', 64), 0m, 0, false, false, true), ct);
            }, default);

        Assert.True(settled.IsSuccess, settled.Error.Message);

        var observed = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(observed.IsSuccess, observed.Error.Message);

        Assert.True(observed.Value.Complete);

        Assert.Equal(0, observed.Value.PendingMembers);

        Assert.Equal(0, observed.Value.PendingDescendants);

        Assert.Equal(0, observed.Value.PendingAccounts);
    }

    [Fact]
    public async Task Cohort_settlement_preserves_exact_known_spend_and_uncertain_remainder_after_evidence_change()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.First, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "finished");

        var before = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(before.IsSuccess, before.Error.Message);

        Assert.True(before.Value.Complete);

        var correction = await f.Authority.InQuestTransactionAsync<GuildBudgetEffect>(root.Quest.RootQuestId,
            async (connection, transaction, _, ct) =>
            {
                await using var command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = "SELECT EffectId FROM guild_budget_effects WHERE QuestId=$quest";

                command.Parameters.AddWithValue("$quest", members[0].Quest.Id.ToString("D").ToUpperInvariant());

                Guid effect = Guid.Parse((string)(await command.ExecuteScalarAsync(ct))!);

                return await new GuildBudgetLedger().ApplyEvidenceWithinAsync(connection, transaction, effect,
                    new(Guid.NewGuid(), new string('A', 64), 0.1234567890123456789012345678m, 37, true, true, true), ct);
            }, default);

        Assert.True(correction.IsSuccess, correction.Error.Message);

        var observed = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(observed.IsSuccess, observed.Error.Message);

        Assert.True(observed.Value.Complete);

        Assert.Equal(0.1234567890123456789012345678m, observed.Value.Snapshot!.KnownUsd);

        Assert.Equal(37, observed.Value.Snapshot.KnownTokens);

        Assert.True(observed.Value.Snapshot.UnknownUsd);

        Assert.True(observed.Value.Snapshot.UnknownTokens);

        Assert.Equal(1, observed.Value.Snapshot.UnpricedCount);

        Assert.Equal(1, observed.Value.Snapshot.UncertainCount);

        Assert.Equal(GuildBudgetMode.Unlimited, observed.Value.Snapshot.UnresolvedHolds.Usd.Mode);

        Assert.Equal(2L, await f.ScalarAsync("SELECT Generation FROM guild_cohort_settlement_state"));

        var expected = await f.Authority.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, cohort.AllocationAccountId, ct), default);

        Assert.True(expected.IsSuccess, expected.Error.Message);

        Assert.Equal(expected.Value, observed.Value.Snapshot);
    }

    [Fact]
    public async Task Cohort_settlement_resumes_a_bounded_page_after_restart_and_invalidates_changed_evidence()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 23);

        var first = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.False(first.Value.Complete);

        Assert.Null(first.Value.Snapshot);

        Assert.InRange(await f.ScalarAsync("SELECT count(*) FROM guild_cohort_settlement_nodes"), 1L, 23L);

        GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

        using SignetAuthority restarted = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

        var initialized = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(initialized.IsSuccess, initialized.Error.Message);

        var complete = first;

        for (int attempt = 0; attempt < 30 && !complete.Value.Complete; attempt++)
        {
            complete = await restarted.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
                (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

            Assert.True(complete.IsSuccess, complete.Error.Message);
        }

        Assert.True(complete.Value.Complete);

        Assert.Equal(23, complete.Value.PendingMembers);

        Assert.Equal(1L, await f.ScalarAsync("SELECT Generation FROM guild_cohort_settlement_state"));

        var expected = await restarted.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, cohort.AllocationAccountId, ct), default);

        Assert.True(expected.IsSuccess, expected.Error.Message);

        Assert.Equal(expected.Value, complete.Value.Snapshot);

        await CommitConvergenceResultAsync(f, owner, members[0], "finished after snapshot");

        var changed = await restarted.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(changed.IsSuccess, changed.Error.Message);

        Assert.False(changed.Value.Complete);

        Assert.Null(changed.Value.Snapshot);

        Assert.Equal(2L, await f.ScalarAsync("SELECT Generation FROM guild_cohort_settlement_state"));

        for (int attempt = 0; attempt < 30 && !changed.Value.Complete; attempt++)
        {
            changed = await restarted.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
                (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

            Assert.True(changed.IsSuccess, changed.Error.Message);
        }

        Assert.True(changed.Value.Complete);

        Assert.Equal(22, changed.Value.PendingMembers);

        expected = await restarted.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, cohort.AllocationAccountId, ct), default);

        Assert.True(expected.IsSuccess, expected.Error.Message);

        Assert.Equal(expected.Value, changed.Value.Snapshot);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_settlement_nodes WHERE Generation<>2"));
    }

    [Fact]
    public async Task Cohort_bounded_settlement_matches_exact_budget_ledger_and_keeps_live_members_visible()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 3);

        await CommitConvergenceResultAsync(f, owner, members[0], "finished");

        var observed = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(observed.IsSuccess, observed.Error.Message);

        Assert.True(observed.Value.Complete);

        Assert.Equal(2, observed.Value.PendingMembers);

        Assert.Equal(0, observed.Value.PendingDescendants);

        var expected = await f.Authority.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, cohort.AllocationAccountId, ct), default);

        Assert.True(expected.IsSuccess, expected.Error.Message);

        Assert.Equal(expected.Value, observed.Value.Snapshot);

        await CommitConvergenceResultAsync(f, owner, members[1], "finished");

        await CommitConvergenceResultAsync(f, owner, members[2], "finished");

        var complete = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(complete.IsSuccess, complete.Error.Message);

        Assert.True(complete.Value.Complete);

        Assert.Equal(0, complete.Value.PendingMembers);

        Assert.False(complete.Value.Snapshot!.UnknownUsd);

        Assert.False(complete.Value.Snapshot.UnknownTokens);
    }
}
