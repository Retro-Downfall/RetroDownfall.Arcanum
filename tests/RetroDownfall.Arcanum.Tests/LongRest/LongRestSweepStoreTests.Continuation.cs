using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Own_canonical_commit_allows_disjoint_discovery_with_live_projection_flags(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 24);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        string originalNodes = (string)(await fixture.H.ScalarAsync("SELECT group_concat(hex(MembershipDigest) || ':' || hex(SourceDigest)) FROM long_rest_discovery_nodes;"))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        string original = (string)(await fixture.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_adjudications;"))!;

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2 AND ReceiptId IS NOT NULL;"));

        LongRestTarget disjoint = await fixture.H.TargetAsync("10000000-0000-0000-0000-000000000024");

        Assert.DoesNotContain(work.Inputs, input => input.Target == disjoint);

        await using (SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: false))
        {
            foreach (LongRestSourceLeaf input in work.Inputs)
            {
                LongRestSourceLeaf current = await LongRestDiscoveryStore.ReadSweepLeafAsync(fixture.H.Connection, transaction,
                    fixture.Generation, input.Target.MemoryId, fixture.Capability, CancellationToken.None);

                Assert.True(current.Snapshot.IsConsolidated || current.Snapshot.IsTransformationOutput);
            }
        }

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId, disjoint, fixture.Capability, CancellationToken.None));

        Assert.Equal(originalNodes, await fixture.H.ScalarAsync("SELECT group_concat(hex(MembershipDigest) || ':' || hex(SourceDigest)) FROM long_rest_discovery_nodes;"));

        string survivor = (string)(await fixture.H.ScalarAsync("SELECT SurvivorMemoryId FROM long_rest_receipts WHERE OutcomeCode = 1;"))!;

        Assert.DoesNotContain(groups.SelectMany(group => group.Targets), target => work.Inputs.Any(input => input.Target == target) && target.MemoryId != survivor);

        Assert.Equal(original, await fixture.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_adjudications;"));

        Assert.Equal(1, fixture.AdjudicationSends);

        await fixture.H.ExecuteAsync("UPDATE long_rest_work_items SET ReceiptId = NULL WHERE KindCode = 3 AND DispositionCode = 2;");

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId, disjoint, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Own_projection_refreshes_only_unaccepted_groups_and_defers_cursor_until_all_anchor_groups_finish(bool protect, bool suppressAnchor)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 24);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        Assert.True((long)(await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"))! > 1);

        string pending = (string)(await fixture.H.ScalarAsync("SELECT ItemId FROM long_rest_work_items WHERE KindCode = 3 ORDER BY ItemId DESC LIMIT 1;"))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work, suppressAnchor ? LongRestAdjudicationKind.Supersession : LongRestAdjudicationKind.EquivalentObservations);

        string original = (string)(await fixture.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_adjudications;"))!;

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        Assert.Null(committed.CommittedItemCursor);

        LongRestSweepWork? next = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(committed, fixture.Capability, CancellationToken.None));

        if (suppressAnchor)
        {
            await using SqliteCommand disposition = fixture.H.Connection.CreateCommand();

            disposition.CommandText = "SELECT DispositionCode = 2 AND ReceiptId IS NULL FROM long_rest_work_items WHERE ItemId = $item;";

            disposition.Parameters.AddWithValue("$item", pending);

            Assert.Equal(1L, await disposition.ExecuteScalarAsync());
        }
        else
        {
            Assert.NotNull(next);

            Assert.Equal(pending, next.Identity.ItemId);

            Assert.Contains(next.Inputs, leaf => leaf.Snapshot.IsTransformationOutput);
        }

        if (next is not null)
        {
            Assert.All(next.Inputs, leaf => Assert.False(leaf.Snapshot.IsConsolidated));
        }

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(original, await fixture.H.ScalarAsync("SELECT EvidenceJson FROM long_rest_adjudications;"));

        Assert.Equal(1, fixture.AdjudicationSends);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("executor")]
    [InlineData("checkpoint")]
    [InlineData("binding")]
    public async Task Locally_valid_capability_cannot_publish_after_durable_claim_changes(string change)
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        if (change == "binding")
        {
            using var authorization = RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionInitializer.Instance.Authorize(
                fixture.H.Connection, RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteAuthorizationKind.OwnerCleanup);

            await fixture.H.ExecuteAsync("DELETE FROM session_campaign_bindings WHERE SessionId = $session;", ("$session", fixture.Capability!.Claim.Claim.SessionId.ToString("D").ToUpperInvariant()));
        }
        else
        {
            string sql = change switch
            {
                "expiry" => "UPDATE session_turn_claims SET LeaseDeadlineUtc = '2000-01-01T00:00:00.0000000Z';",
                "executor" => "UPDATE session_turn_claims SET ExecutorId = '99999999-9999-9999-9999-999999999999';",
                _ => "UPDATE session_turn_claims SET CheckpointRevision = CheckpointRevision + 1;",
            };

            await fixture.H.ExecuteAsync(sql);
        }

        Assert.True((await fixture.Capability!.ValidateWorkAsync(work.Identity, work.Identity.Fence.Configuration, CancellationToken.None)).IsSuccess);

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.True((await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));
    }

    [Fact]
    public async Task Protected_Campaign_application_preserves_historical_clean_Annals_and_canonical_owner_labels()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true, campaign: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        Assert.All(work.Inputs, leaf => Assert.Equal(RetroDownfall.Arcanum.Core.Covenant.ContentSensitivity.None, leaf.Snapshot.Sensitivity));

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts WHERE PolicyVersion = 2 AND OutcomeCode = 1;"));

        Assert.Equal(2L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_protected_receipt_inputs WHERE SagaRevision = 37;"));
    }
}
