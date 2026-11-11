using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(GuildConvergenceKind.All)]
    [InlineData(GuildConvergenceKind.First)]
    [InlineData(GuildConvergenceKind.Quorum)]
    public async Task Cohort_reconciliation_respects_closed_membership_and_result_sequence(GuildConvergenceKind kind)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, kind,
            kind == GuildConvergenceKind.Quorum ? 2 : null, "text/plain", 2);

        // Results are committed while membership is open, in reverse admission order.
        await CommitConvergenceResultAsync(f, owner, members[1], "shared output");

        await CommitConvergenceResultAsync(f, owner, members[0], "shared output");

        var open = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.True(open.IsSuccess, open.Error.Message);

        Assert.Null(open.Value.Decision);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto decided = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Success, decided.Decision);

        Assert.Equal(2, decided.FrozenMemberCount);

        string second = members[1].Quest.Id.ToString("D");

        Assert.Equal(kind == GuildConvergenceKind.First ? 1L : 2L,
            await f.ScalarAsync("""
                SELECT count(*) FROM guild_cohort_selections s
                JOIN guild_cohort_eval_candidates c ON c.CohortId=s.CohortId AND c.Generation=s.Generation
                WHERE c.OutcomeCode=1 AND (s.KindCode=1 OR s.KindCode=2 AND c.ResultSequence=s.ThroughSequence
                    OR s.KindCode=3 AND c.BucketId=s.WinningBucket AND c.ResultSequence<=s.ThroughSequence)
                """));

        Assert.Equal(1L, await f.ScalarAsync($"""
                SELECT count(*) FROM guild_cohort_selections s
                JOIN guild_cohort_eval_candidates c ON c.CohortId=s.CohortId AND c.Generation=s.Generation
                JOIN guild_cohort_members m ON m.Id=c.MemberId
                WHERE m.PrimaryVotingQuestId='{second}' COLLATE NOCASE AND c.ResultSequence=1 AND c.OutcomeCode=1
                  AND (s.KindCode=1 OR s.KindCode=2 AND c.ResultSequence=s.ThroughSequence
                    OR s.KindCode=3 AND c.BucketId=s.WinningBucket AND c.ResultSequence<=s.ThroughSequence)
                """));

        _ = root;
    }

    [Fact]
    public async Task Cohort_quorum_uses_exact_json_bytes_and_proves_impossibility()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.Quorum, 2, "application/json", 2);

        await CommitConvergenceResultAsync(f, owner, members[0], "{\"a\":1}");

        await CommitConvergenceResultAsync(f, owner, members[1], "{ \"a\": 1 }");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto decided = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Impossible, decided.Decision);

        Assert.Equal(2, decided.FrozenMemberCount);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections"));
    }

    [Fact]
    public async Task Cohort_incremental_results_preserve_the_valid_staged_prefix()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 3);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await CommitConvergenceResultAsync(f, owner, members[0], "first");

        _ = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.PendingMemberCount == 2);

        long generation = await f.ScalarAsync("SELECT Generation FROM guild_cohort_eval_state");

        Assert.Equal(1L, await f.ScalarAsync("SELECT LastResultSequence FROM guild_cohort_eval_state"));

        await CommitConvergenceResultAsync(f, owner, members[1], "second");

        _ = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.PendingMemberCount == 1);

        Assert.Equal(generation, await f.ScalarAsync("SELECT Generation FROM guild_cohort_eval_state"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT LastResultSequence FROM guild_cohort_eval_state"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_eval_candidates"));

        await CommitConvergenceResultAsync(f, owner, members[2], "third");

        CohortConvergenceDto decided = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Success, decided.Decision);

        Assert.Equal(generation, await f.ScalarAsync("SELECT Generation FROM guild_cohort_eval_state"));
    }

    [Fact]
    public async Task Cohort_decision_remains_draining_when_completed_work_has_unknown_paid_exposure()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.First, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "valid answer", recordBilling: false);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto result = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Success, result.Decision);

        Assert.Equal(GuildCohortState.Draining, result.State);

        Assert.True(result.Spend.UnknownUsd || result.Spend.UnknownTokens || result.Spend.UncertainCount > 0);

        Assert.Equal(0L, await f.ScalarAsync($"SELECT Closed FROM guild_budget_accounts WHERE AccountId='{cohort.AllocationAccountId:D}' COLLATE NOCASE"));
    }

    [Fact]
    public async Task Cohort_first_selection_stays_draining_with_an_unsettled_loser()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.First, null, "text/plain", 2);

        await CommitConvergenceResultAsync(f, owner, members[0], "selected");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto result = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Success, result.Decision);

        Assert.Equal(GuildCohortState.Draining, result.State);

        Assert.True(result.PendingMemberCount > 0 || result.PendingDescendantCount > 0);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_stop_frontiers WHERE MemberId IS NULL"));
    }

    [Fact]
    public async Task Cohort_paged_reconciliation_restarts_if_a_staged_candidate_is_erased()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 101);

        foreach (GuildQuestStored member in members)
            await CommitConvergenceResultAsync(f, owner, member, "same answer");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        var page = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.True(page.IsSuccess || page.Error.Code is ErrorCodes.Guild.AuthorityUnavailable or ErrorCodes.Guild.MaintenanceDeferred, page.Error.Message);

        if (page.IsSuccess) Assert.Null(page.Value.Decision);

        Assert.True(await f.ScalarAsync("SELECT LastResultSequence FROM guild_cohort_eval_state") > 0);

        await using (var connection = await f.OpenAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using var select = connection.CreateCommand();

            select.Transaction = transaction;

            select.CommandText = "SELECT ContentId FROM guild_cohort_contributions ORDER BY ResultSequence LIMIT 1";

            Guid content = Guid.Parse(Assert.IsType<string>(await select.ExecuteScalarAsync()));

            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, content.ToString("N"), default);

            await using var delete = connection.CreateCommand();

            delete.Transaction = transaction;

            delete.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

            delete.Parameters.AddWithValue("$id", content.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await delete.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

        using SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

        GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority,
            restartedPolicy, new GuildBudgetLedger(), TimeProvider.System);

        GuildCohortStore restartedCohorts = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority,
            restartedStore, new GuildBudgetLedger(), restartedPolicy, TimeProvider.System);

        var restartedOwner = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(restartedOwner.IsSuccess, restartedOwner.Error.Message);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            page = await restartedCohorts.ReconcileAsync(restartedOwner.Value, cohort.Id, default);

            Assert.True(page.IsSuccess || page.Error.Code is ErrorCodes.Guild.AuthorityUnavailable or ErrorCodes.Guild.MaintenanceDeferred, page.Error.Message);

            if (await f.ScalarAsync("SELECT COALESCE(DecisionCode,0) FROM guild_cohorts") != 0) break;
        }

        Assert.Equal((long)GuildConvergenceDecision.Impossible,
            await f.ScalarAsync("SELECT COALESCE(DecisionCode,0) FROM guild_cohorts"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections"));

        Assert.Equal(0L, await f.ScalarAsync("""
            SELECT count(*) FROM guild_cohort_eval_candidates c JOIN guild_cohort_eval_state s ON s.CohortId=c.CohortId
            WHERE c.Generation<s.Generation
            """));

        Assert.Equal(0L, await f.ScalarAsync("""
            SELECT count(*) FROM guild_cohort_eval_buckets b JOIN guild_cohort_eval_state s ON s.CohortId=b.CohortId
            WHERE b.Generation<s.Generation
            """));
    }

    [Fact]
    public async Task Cohort_invalidated_staging_retires_in_bounded_pages()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        await CommitConvergenceResultAsync(f, owner, members[0], "will erase");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True((await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default)).IsSuccess);

        _ = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.PendingMemberCount == 1);

        await using (var connection = await f.OpenAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using var select = connection.CreateCommand();

            select.Transaction = transaction;

            select.CommandText = "SELECT ContentId FROM guild_cohort_contributions LIMIT 1";

            Guid content = Guid.Parse(Assert.IsType<string>(await select.ExecuteScalarAsync()));

            await using var seed = connection.CreateCommand();

            seed.Transaction = transaction;

            seed.CommandText = """
                WITH RECURSIVE n(x) AS (SELECT 2 UNION ALL SELECT x+1 FROM n WHERE x<102)
                INSERT INTO guild_cohort_eval_buckets(CohortId,Generation,BucketId,AgreementDigest,RepresentativeContentId,VoteCount)
                SELECT s.CohortId,s.Generation,n.x,randomblob(32),$content,1 FROM guild_cohort_eval_state s CROSS JOIN n
                """;

            seed.Parameters.AddWithValue("$content", content.ToString("D").ToUpperInvariant());

            Assert.Equal(101, await seed.ExecuteNonQueryAsync());

            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, content.ToString("N"), default);

            await using var delete = connection.CreateCommand();

            delete.Transaction = transaction;

            delete.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

            delete.Parameters.AddWithValue("$id", content.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await delete.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        var first = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.True(first.IsSuccess || first.Error.Code is ErrorCodes.Guild.MaintenanceDeferred or ErrorCodes.Guild.AuthorityUnavailable, first.Error.Message);

        long remaining = await f.ScalarAsync("SELECT count(*) FROM guild_cohort_eval_buckets b JOIN guild_cohort_eval_state s ON s.CohortId=b.CohortId WHERE b.Generation<s.Generation");

        Assert.InRange(remaining, 2, 101);

        for (int attempt = 0; attempt < 8 && remaining > 0; attempt++)
        {
            _ = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            remaining = await f.ScalarAsync("SELECT count(*) FROM guild_cohort_eval_buckets b JOIN guild_cohort_eval_state s ON s.CohortId=b.CohortId WHERE b.Generation<s.Generation");
        }

        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task Cohort_candidate_with_compact_alias_label_defers_without_selecting()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "private candidate");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await using (var connection = await f.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO artifact_sensitivity(LabelId,ArtifactKindCode,ArtifactId,SensitivityCode,ProvenanceModeCode,
                    ExactGenerationIds,ArtifactRevision,ArtifactContentDigest,SensitivityDigest,ArtifactLabelDigest,CreatedAtUtc)
                SELECT $label,17,lower(replace(ContentId,'-','')),1,1,randomblob(16),1,zeroblob(32),zeroblob(32),zeroblob(32),
                    '2026-11-10T00:00:00.0000000Z' FROM guild_cohort_contributions WHERE CohortId=$cohort
                """;

            command.Parameters.AddWithValue("$label", Guid.NewGuid().ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$cohort", cohort.Id.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var result = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            Assert.True(result.IsFailure || result.Value.Decision is null);
        }

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections"));
    }

    [Fact]
    public async Task Cohort_erased_candidate_cannot_be_selected_from_retained_digest()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "erased answer");

        await using (var connection = await f.OpenAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using var select = connection.CreateCommand();

            select.Transaction = transaction;

            select.CommandText = "SELECT ContentId FROM guild_cohort_contributions";

            Guid content = Guid.Parse(Assert.IsType<string>(await select.ExecuteScalarAsync()));

            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, content.ToString("N"), default);

            await using var delete = connection.CreateCommand();

            delete.Transaction = transaction;

            delete.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

            delete.Parameters.AddWithValue("$id", content.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await delete.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto result = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Impossible, result.Decision);

        Assert.Null(result.SelectedResultRef);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_selections"));
    }

    [Fact]
    public async Task Cohort_preclose_withdrawn_result_is_historical_not_a_frozen_vote()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 3);

        await CommitConvergenceResultAsync(f, owner, members[1], "historical");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        await using var connection = await f.OpenAsync();

        await using var query = connection.CreateCommand();

        query.CommandText = "SELECT Id FROM guild_cohort_members WHERE PrimaryVotingQuestId=$quest";

        query.Parameters.AddWithValue("$quest", members[1].Quest.Id.ToString("D").ToUpperInvariant());

        Guid member = Guid.Parse(Assert.IsType<string>(await query.ExecuteScalarAsync()));

        var removed = await cohorts.RemoveAsync(issuer, cohort.Id, member, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(removed.IsSuccess, removed.Error.Message);

        current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        CohortConvergenceDto waiting = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.State == GuildCohortState.Closed);

        Assert.Null(waiting.Decision);

        Assert.Equal(2, waiting.FrozenMemberCount);

        Assert.True(waiting.PendingMemberCount >= 2); // Withdrawal remains a separate drain obligation.

        Assert.Equal(1L, await f.ScalarAsync("SELECT LastResultSequence FROM guild_cohort_eval_state"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT ProcessedCount FROM guild_cohort_eval_state"));

        await CommitConvergenceResultAsync(f, owner, members[0], "one");

        await CommitConvergenceResultAsync(f, owner, members[2], "two");

        CohortConvergenceDto decided = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Success, decided.Decision);

        Assert.Equal(2, decided.FrozenMemberCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_cleanup_does_not_refresh_pending_when_effect_remains_unresolved(bool live)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (_, _, _, _, _, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var owner = await f.OperatorAsync();

        await CommitConvergenceResultAsync(f, owner, members[0], "done", recordBilling: false);

        await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode=1,Revision=Revision+1 WHERE Id='{members[0].Quest.Id:D}' COLLATE NOCASE");

        if (live)
        {
            // Deliberately inject a stranded live effect after account closure to test
            // the cleanup proof independently of the ordinary account-open guard.
            await f.ExecuteAsync("DROP TRIGGER guild_budget_effects_guard_update; UPDATE guild_budget_effects SET StateCode=1,UnknownUsd=0,UnknownTokens=0");
        }

        GuildCleanupOwner cleanupOwner = new(2, members[0].Quest.SessionId!.Value, null);

        var result = await new GuildQuestCleanupParticipant(f.Connections, TimeProvider.System).ReconcileAsync(cleanupOwner, default);

        Assert.True(result.IsFailure);

        Assert.Equal(1L, await f.ScalarAsync($"SELECT AccountingStateCode FROM guild_quests WHERE Id='{members[0].Quest.Id:D}' COLLATE NOCASE"));
    }

    [Fact]
    public async Task Owner_cleanup_refreshes_settled_member_without_waiting_for_live_sibling()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (_, _, _, _, _, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        var owner = await f.OperatorAsync();

        await CommitConvergenceResultAsync(f, owner, members[0], "done");

        await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode=1,Revision=Revision+1 WHERE Id='{members[0].Quest.Id:D}' COLLATE NOCASE");

        GuildCleanupOwner cleanupOwner = new(2, members[0].Quest.SessionId!.Value, null);

        _ = await new GuildQuestCleanupParticipant(f.Connections, TimeProvider.System).ReconcileAsync(cleanupOwner, default);

        Assert.Equal(0L, await f.ScalarAsync($"SELECT AccountingStateCode FROM guild_quests WHERE Id='{members[0].Quest.Id:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_budget_accounts a JOIN guild_quests q ON q.AllocationAccountId=a.AccountId WHERE q.Id='{members[1].Quest.Id:D}' COLLATE NOCASE AND a.Closed=0"));
    }

    [Fact]
    public async Task Cohort_postclose_withdrawal_preserves_the_frozen_quorum_electorate()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.Quorum, 2, "text/plain", 3);

        await CommitConvergenceResultAsync(f, owner, members[0], "same");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await using var connection = await f.OpenAsync();

        await using var query = connection.CreateCommand();

        query.CommandText = "SELECT Id FROM guild_cohort_members WHERE PrimaryVotingQuestId=$quest";

        query.Parameters.AddWithValue("$quest", members[1].Quest.Id.ToString("D").ToUpperInvariant());

        Guid memberId = Guid.Parse(Assert.IsType<string>(await query.ExecuteScalarAsync()));

        var removed = await cohorts.RemoveAsync(issuer, cohort.Id, memberId, new(Guid.NewGuid(), closed.Value.Revision), default);

        Assert.True(removed.IsSuccess, removed.Error.Message);

        CohortConvergenceDto waiting = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.State == GuildCohortState.Closed);

        Assert.Null(waiting.Decision);

        Assert.Equal(3, waiting.FrozenMemberCount);

        Assert.True(waiting.PendingMemberCount >= 1); // A withdrawn member may still be draining.

        Assert.Equal(2L, await f.ScalarAsync("SELECT ProcessedCount FROM guild_cohort_eval_state"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions WHERE OutcomeCode=5"));

        await CommitConvergenceResultAsync(f, owner, members[2], "same");

        CohortConvergenceDto decided = await WaitForConvergenceAsync(cohorts, owner, cohort.Id, value => value.Decision is not null);

        Assert.Equal(GuildConvergenceDecision.Success, decided.Decision);

        Assert.Equal(3, decided.FrozenMemberCount);
    }

    private static async Task<CohortConvergenceDto> WaitForConvergenceAsync(GuildCohortStore cohorts, GuildCallerContext owner,
        Guid cohort, Func<CohortConvergenceDto, bool> predicate)
    {
        string last = "no result";

        for (int attempt = 0; attempt < 40; attempt++)
        {
            var result = await cohorts.ReconcileAsync(owner, cohort, default);

            Assert.True(result.IsSuccess || result.Error.Code is ErrorCodes.Guild.AuthorityUnavailable or ErrorCodes.Guild.MaintenanceDeferred, result.Error.Message);

            if (result.IsSuccess && predicate(result.Value)) return result.Value;

            last = result.IsSuccess
                ? $"state={result.Value.State}, decision={result.Value.Decision}, pendingMembers={result.Value.PendingMemberCount}, pendingDescendants={result.Value.PendingDescendantCount}"
                : $"{result.Error.Code}: {result.Error.Message}";
        }

        throw new Xunit.Sdk.XunitException("Cohort reconciliation did not converge within the test's bounded attempts: " + last);
    }

    private static async Task<(GuildCallerContext Owner, GuildCallerContext Issuer, GuildQuestStored Root,
        GuildCohortStore Cohorts, CohortDto Cohort, List<GuildQuestStored> Members)> ConvergenceFixtureAsync(
        Fixture f, GuildConvergenceKind kind, int? quorum, string mime, int count)
    {
        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
        {
            Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort],
        } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext issuer = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        var created = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), root.Value.Quest.Id, "Convergence",
            new(kind, quorum), new(mime), null), default);

        Assert.True(created.IsSuccess, created.Error.Message);

        List<GuildQuestStored> members = [];

        for (int index = 0; index < count; index++)
        {
            var rootCurrent = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

            var cohortCurrent = await cohorts.GetAsync(issuer, created.Value.Id, default);

            Assert.True(rootCurrent.IsSuccess, rootCurrent.Error.Message);

            Assert.True(cohortCurrent.IsSuccess, cohortCurrent.Error.Message);

            Guid key = Guid.NewGuid();

            var member = await f.Store.IssueCohortMemberAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, key, key),
                created.Value.Id, cohortCurrent.Value.Revision,
                new(key, rootCurrent.Value.Quest.Revision, null, "Member " + index, "Contribute", new()), default);

            Assert.True(member.IsSuccess, member.Error.Message);

            members.Add(member.Value);
        }

        return (owner, issuer, root.Value, cohorts, created.Value, members);
    }

    private static async Task CommitConvergenceResultAsync(Fixture f, GuildCallerContext owner, GuildQuestStored quest, string value,
        GuildCallerContext? retainedRun = null, QuestState outcome = QuestState.Completed, bool recordBilling = true)
    {
        GuildCallerContext run = retainedRun ?? await BindCohortRunAsync(f, owner, quest);

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        string leaseOwner = "convergence-" + Guid.NewGuid().ToString("N");

        var lease = await operations.TryAcquireLeaseAsync(quest.Quest.OperationId, leaseOwner, now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        GuildQuestDriver driver = new(run.Execution!, leaseOwner, lease.Operation.Revision, run);

        string invocation = Convert.ToHexString(SHA256.HashData("convergence dispatch"u8));

        var prepared = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), invocation, Guid.NewGuid(), false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        GuildBudgetLedger ledger = new();

        Guid effectId = Guid.NewGuid();

        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var effect = await ledger.PrepareEffectWithinAsync(connection, transaction,
                new(effectId, quest.Quest.Id, prepared.Value.DispatchId, prepared.Value.AccountingAssociationId,
                    GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null), default);

            Assert.True(effect.IsSuccess, effect.Error.Message);

            await transaction.CommitAsync();
        }

        var possible = await f.Store.MarkPossiblyDispatchedAsync(driver, prepared.Value.DispatchId, prepared.Value.Revision, default);

        Assert.True(possible.IsSuccess, possible.Error.Message);

        var confirmed = await f.Store.RecordDispatchEvidenceAsync(driver,
            new(prepared.Value.DispatchId, possible.Value.Dispatch.Revision, GuildDispatchEvidence.Confirmed, LocalChildId: prepared.Value.LocalChildId), default);

        Assert.True(confirmed.IsSuccess, confirmed.Error.Message);

        {
            await using var transaction = connection.BeginTransaction(deferred: false);

            var evidence = await ledger.ApplyEvidenceWithinAsync(connection, transaction, effectId,
                new(Guid.NewGuid(), invocation, 0m, 0, !recordBilling, !recordBilling, true), default);

            Assert.True(evidence.IsSuccess, evidence.Error.Message);

            await transaction.CommitAsync();
        }

        var current = await f.Store.GetAsync(owner, quest.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        byte[] bytes = Encoding.UTF8.GetBytes(value);

        string digest = Convert.ToHexString(SHA256.HashData(bytes));

        GuildDataRef content = new(Guid.NewGuid(), GuildContentKind.QuestResult, 1, digest, "text/plain", bytes.Length, false);

        GuildQuestResult result = new(new(quest.Quest.Id, Guid.NewGuid(), digest, content, outcome,
            outcome == QuestState.Failed ? ErrorCodes.Sending.TaskRejected : null), bytes,
            WorkStoppedProven: true, DesignatedResultCount: 1, DesignatedResultUtf8: bytes, DesignatedResultMimeType: "text/plain");

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), digest), current.Value.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);
    }
}
