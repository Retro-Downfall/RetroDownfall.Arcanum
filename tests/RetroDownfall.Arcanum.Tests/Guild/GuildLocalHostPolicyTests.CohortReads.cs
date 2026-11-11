using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cohort_selected_pages_keep_admission_order_and_recheck_current_reader_authority()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 2);

        await CommitConvergenceResultAsync(f, owner, members[1], "second");

        await CommitConvergenceResultAsync(f, owner, members[0], "first");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        // A retained decision is the reader's input; policy evaluation has its independent tests.
        await FreezeSelectedReadFixtureAsync(f);

        var first = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(members[0].Quest.Id, Assert.Single(first.Value.Items).PrimaryVotingQuestId);

        Assert.NotNull(first.Value.Items[0].ResultRef);

        Assert.Equal(1L, first.Value.NextAdmissionSequence);

        var second = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, first.Value.NextAdmissionSequence!.Value, 1, default);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(members[1].Quest.Id, Assert.Single(second.Value.Items).PrimaryVotingQuestId);

        Assert.Null(second.Value.NextAdmissionSequence);

        Assert.Equal(first.Value.ClosedMembershipVersion, second.Value.ClosedMembershipVersion);

        var widened = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 101, default);

        Assert.True(widened.IsFailure);

        await f.ExecuteAsync($"UPDATE guild_signets SET Revoked=1,Revision=Revision+1,RevocationGeneration=RevocationGeneration+1 WHERE QuestId='{root.Quest.Id:D}' COLLATE NOCASE");

        var refused = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 1, 1, default);

        Assert.True(refused.IsFailure);

        Assert.Contains(refused.Error.Code, new[] { ErrorCodes.Guild.AuthorityRevoked, ErrorCodes.Guild.Forbidden });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cohort_selected_read_redacts_newly_protected_source_or_contribution(bool source, bool compactLabel)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, cohorts, cohort, member) = await SelectedReadFixtureAsync(f);

        var before = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(before.IsSuccess, before.Error.Message);

        GuildDataRef selected = Assert.Single(before.Value.Items).ResultRef!;

        var current = await f.Store.GetAsync(owner, member.Quest.Id, default);

        GuildDataRef target = source ? current.Value.Quest.ResultRef! : selected;

        await using (var connection = await f.OpenAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            DerivedArtifactWrite label = new(SensitiveArtifactKind.GuildContent, target.ContentId,
                member.Quest.SessionId, member.Quest.CampaignId, null, (ulong)target.Revision,
                new CovenantDigest(Convert.FromHexString(target.Digest)), ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()]));

            var written = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, label, default);

            Assert.True(written.IsSuccess, written.Error.Message);

            if (compactLabel)
            {
                // Install a historical identifier spelling in this corruption fixture; production
                // labels remain immutable. The reader must still recognize this protection.
                await using var legacy = connection.CreateCommand();

                legacy.Transaction = transaction;

                legacy.CommandText = "DROP TRIGGER artifact_sensitivity_guard_update";

                await legacy.ExecuteNonQueryAsync();

                await using var alias = connection.CreateCommand();

                alias.Transaction = transaction;

                alias.CommandText = "UPDATE artifact_sensitivity SET ArtifactId=lower(replace(ArtifactId,'-','')) WHERE ArtifactKindCode=17 AND lower(replace(ArtifactId,'-',''))=$id";

                alias.Parameters.AddWithValue("$id", target.ContentId.ToString("N"));

                Assert.Equal(1, await alias.ExecuteNonQueryAsync());

                legacy.CommandText = """
                    CREATE TRIGGER artifact_sensitivity_guard_update
                    BEFORE UPDATE ON artifact_sensitivity
                    BEGIN
                        SELECT RAISE(ABORT, 'artifact_sensitivity rows are immutable; a new artifact revision needs a new label.');
                    END;
                    """;

                await legacy.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var after = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(after.IsSuccess, after.Error.Message);

        Assert.Null(Assert.Single(after.Value.Items).ResultRef);

        Assert.Null(after.Value.Items[0].AgreementDigest);

        Assert.True(after.Value.Items[0].Selected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cohort_selected_read_redacts_erased_source_or_contribution_without_reconstruction(bool source)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, cohorts, cohort, member) = await SelectedReadFixtureAsync(f);

        var before = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(before.IsSuccess, before.Error.Message);

        var current = await f.Store.GetAsync(owner, member.Quest.Id, default);

        Guid content = source ? current.Value.Quest.ResultRef!.ContentId : Assert.Single(before.Value.Items).ResultRef!.ContentId;

        await using (var connection = await f.OpenAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, content.ToString("N"), default);

            await using var delete = connection.CreateCommand();

            delete.Transaction = transaction;

            delete.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

            delete.Parameters.AddWithValue("$id", content.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await delete.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        var after = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(after.IsSuccess, after.Error.Message);

        Assert.Null(Assert.Single(after.Value.Items).ResultRef);

        Assert.Null(after.Value.Items[0].AgreementDigest);
    }

    [Fact]
    public async Task Cohort_selected_read_refuses_erased_source_owner_and_unrelated_or_native_caller()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, cohorts, cohort, member) = await SelectedReadFixtureAsync(f);

        Assert.True((await cohorts.ReadSelectedContributionsPageAsync(owner, cohort.Id, 0, 1, default)).IsFailure);

        var root = await f.Store.GetAsync(owner, issuer.Execution!.QuestId, default);

        Guid key = Guid.NewGuid();

        var sibling = await f.Store.IssueAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, key, key),
            root.Value.Quest.Id, new(key, root.Value.Quest.Revision, null, "Unrelated child", "No Cohort read authority", new()), default);

        Assert.True(sibling.IsSuccess, sibling.Error.Message);

        var unrelated = await BindCohortRunAsync(f, owner, sibling.Value);

        Assert.True((await cohorts.ReadSelectedContributionsPageAsync(unrelated, cohort.Id, 0, 1, default)).IsFailure);

        await using (var connection = await f.OpenAsync())
        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        {
            await using var delete = connection.CreateCommand();

            delete.CommandText = "DELETE FROM Sessions WHERE replace(lower(Id),'-','')=$id";

            delete.Parameters.AddWithValue("$id", member.Quest.SessionId!.Value.ToString("N"));

            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        Assert.True((await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default)).IsFailure);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("source")]
    [InlineData("proof")]
    [InlineData("stage-sequence")]
    public async Task Cohort_selected_read_refuses_corrupt_immutable_links_instead_of_omitting_selected_member(string corrupt)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (_, issuer, cohorts, cohort, _) = await SelectedReadFixtureAsync(f);

        await f.ExecuteAsync("DROP TRIGGER IF EXISTS guild_cohort_contributions_guard_update; DROP TRIGGER IF EXISTS guild_content_guard_update; DROP TRIGGER IF EXISTS guild_cohort_eval_candidates_guard_update");

        await f.ExecuteAsync(corrupt switch
        {
            "reference" => $"UPDATE guild_cohort_contributions SET ContentId='{Guid.NewGuid():D}'",
            "source" => $"UPDATE guild_content SET SourceRecordId='{Guid.NewGuid().ToString("D").ToUpperInvariant()}' WHERE ContentKindCode={(int)GuildContentKind.CohortContribution}",
            "proof" => "UPDATE guild_cohort_contributions SET ProofDigest=zeroblob(32)",
            _ => "UPDATE guild_cohort_eval_candidates SET ResultSequence=ResultSequence+100",
        });

        var refused = await cohorts.ReadSelectedContributionsPageAsync(issuer, cohort.Id, 0, 1, default);

        Assert.True(refused.IsFailure);
    }

    private static Task FreezeSelectedReadFixtureAsync(Fixture f) => f.ExecuteAsync("""
        UPDATE guild_cohorts SET StateCode=3,DecisionCode=1,Revision=Revision+1;
        INSERT INTO guild_cohort_eval_candidates(CohortId,Generation,MemberId,ResultSequence,OutcomeCode,BucketId)
            SELECT CohortId,1,MemberId,ResultSequence,OutcomeCode,NULL FROM guild_cohort_contributions;
        INSERT INTO guild_cohort_selections(CohortId,Generation,KindCode,WinningBucket,ThroughSequence)
            SELECT Id,1,PolicyCode,NULL,NULL FROM guild_cohorts;
        """);

    private static async Task<(GuildCallerContext Owner, GuildCallerContext Issuer, GuildCohortStore Cohorts,
        CohortDto Cohort, GuildQuestStored Member)> SelectedReadFixtureAsync(Fixture f)
    {
        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f,
            GuildConvergenceKind.All, null, "text/plain", 1);

        await CommitConvergenceResultAsync(f, owner, members[0], "selected bytes");

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Id, new(Guid.NewGuid(), current.Value.Revision), default);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        await FreezeSelectedReadFixtureAsync(f);

        return (owner, issuer, cohorts, cohort, members[0]);
    }
}
