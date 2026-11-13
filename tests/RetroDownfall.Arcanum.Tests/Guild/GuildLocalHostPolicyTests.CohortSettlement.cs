using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(GuildConvergenceKind.All)]
    [InlineData(GuildConvergenceKind.First)]
    public async Task Cohort_finalizes_completed_unrestricted_members_with_unknown_reporting(GuildConvergenceKind kind)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, kind, null, "text/plain", 2);

        await CommitConvergenceResultAsync(f, owner, members[0], "first", recordBilling: false);

        await CommitConvergenceResultAsync(f, owner, members[1], "second", recordBilling: false);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests q JOIN guild_budget_accounts a ON a.AccountId=q.AllocationAccountId JOIN guild_result_receipts r ON r.QuestId=q.Id WHERE q.ParentQuestId IS NOT NULL AND a.Closed=1 AND r.WorkStoppedProven=1 AND q.AccountingStateCode=0"));

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto pending = await WaitForConvergenceAsync(cohorts, owner, cohort.Id,
            value => value.State == GuildCohortState.Draining && value.PendingMemberCount == 2);

        Assert.Equal(2, pending.PendingMemberCount);

        // This is the stop-frontier stage driven by the scheduler after Cohort cancellation.
        // Reconciliation alone cannot persist the independently proved stop convergence.
        foreach (GuildQuestStored member in members)
        {
            var stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, member.Quest.Id, default);

            Assert.True(stopped.IsSuccess, stopped.Error.Message);

            Assert.True(stopped.Value);
        }

        CohortConvergenceDto final = await WaitForConvergenceAsync(cohorts, owner, cohort.Id,
            value => value.State == GuildCohortState.Completed);

        Assert.Equal(GuildConvergenceDecision.Success, final.Decision);

        Assert.True(final.Spend.UnknownUsd);

        Assert.True(final.Spend.UnknownTokens);
    }

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
    public async Task Cohort_settlement_recomputes_after_unrestricted_completed_evidence_changes()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.First, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "stopped with uncertain accounting", recordBilling: false);

        var parent = await f.Store.GetAsync(owner, members[0].Quest.Id, default);

        Assert.True(parent.IsSuccess, parent.Error.Message);

        Assert.Equal(GuildAccountingState.Settled, parent.Value.Quest.AccountingState);

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

        Assert.Equal(0m, observed.Value.Snapshot.UnresolvedHolds.Usd.Value);

        Assert.Equal(2L, await f.ScalarAsync("SELECT Generation FROM guild_cohort_settlement_state"));

        var expected = await f.Authority.InQuestTransactionAsync<GuildBudgetSnapshot>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => new GuildBudgetLedger().ReadWithinAsync(connection, transaction, cohort.AllocationAccountId, ct), default);

        Assert.True(expected.IsSuccess, expected.Error.Message);

        Assert.Equal(expected.Value, observed.Value.Snapshot);
    }

    [Fact]
    public async Task Cohort_settlement_validates_funding_ancestry_once_per_generation()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (_, _, _, _, cohort, _) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 23);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        var first = await GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.False(first.Value.Complete);

        Assert.Single(statements, sql => sql.Contains("WITH RECURSIVE lineage(Id)", StringComparison.Ordinal));

        Assert.Single(statements, sql => sql.Contains("WITH RECURSIVE ancestors(Id)", StringComparison.Ordinal));

        statements.Clear();

        var resumed = await GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, default);

        Assert.True(resumed.IsSuccess, resumed.Error.Message);

        Assert.DoesNotContain(statements, sql => sql.Contains("WITH RECURSIVE lineage(Id)", StringComparison.Ordinal)
            || sql.Contains("WITH RECURSIVE ancestors(Id)", StringComparison.Ordinal));

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23)]
    public async Task Cohort_settlement_rebuilds_unversioned_complete_and_partial_scratch(int count)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (_, _, root, _, cohort, _) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", count);

        var first = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(count == 1, first.Value.Complete);

        await using (var connection = await f.OpenAsync())
        {
            await using var corrupt = connection.CreateCommand();

            corrupt.CommandText = "UPDATE guild_cohort_settlement_nodes SET NodeJson=json_remove(NodeJson,'$.FormatVersion')";

            Assert.True(await corrupt.ExecuteNonQueryAsync() > 0);
        }

        var rebuilt = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(rebuilt.IsSuccess, rebuilt.Error.Message);

        Assert.Equal(2L, await f.ScalarAsync("SELECT Generation FROM guild_cohort_settlement_state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cohort_settlement_refuses_future_or_mixed_scratch_versions(bool partial)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (_, _, root, _, cohort, _) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", partial ? 23 : 1);

        var first = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(!partial, first.Value.Complete);

        await using (var connection = await f.OpenAsync())
        {
            await using var corrupt = connection.CreateCommand();

            corrupt.CommandText = partial
                ? "UPDATE guild_cohort_settlement_nodes SET NodeJson=json_set(NodeJson,'$.FormatVersion',99) WHERE AccountId=(SELECT CurrentAccountId FROM guild_cohort_settlement_state)"
                : "UPDATE guild_cohort_settlement_nodes SET NodeJson=json_set(NodeJson,'$.FormatVersion',99) WHERE AccountId=$root";

            corrupt.Parameters.AddWithValue("$root", cohort.AllocationAccountId.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var refused = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(refused.IsFailure);
    }

    [Theory]
    [InlineData("FiniteUsd")]
    [InlineData("FiniteTokens")]
    public async Task Cohort_settlement_refuses_policy_bits_inconsistent_with_funding_account(string property)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (_, _, root, _, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var first = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        await using (var connection = await f.OpenAsync())
        {
            await using var corrupt = connection.CreateCommand();

            corrupt.CommandText = $"UPDATE guild_cohort_settlement_nodes SET NodeJson=json_set(NodeJson,'$.{property}',json('true')) WHERE AccountId=$root";

            corrupt.Parameters.AddWithValue("$root", cohort.AllocationAccountId.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var refused = await f.Authority.InQuestTransactionAsync<GuildCohortSettlementStep>(root.Quest.RootQuestId,
            (connection, transaction, _, ct) => GuildCohortStore.AdvanceSettlementWithinAsync(connection, transaction, cohort, ct), default);

        Assert.True(refused.IsFailure);
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
