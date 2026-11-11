using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Unavailable_member_issuer_closes_offers_without_award_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (owner, _, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        await AddAuthorityScanOfferMetadataAsync(f, cohort.Id, members[0].Quest.Id, [(Guid.NewGuid(), 1)]);

        Guid key = Guid.NewGuid();

        var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), members[0].Quest.SignetId,
            new(key, members[0].Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);

        for (int step = 0; step < 2; step++)
        {
            var progress = await cohorts.ReconcileAsync(owner, cohort.Id, default);

            Assert.True(progress.IsSuccess || progress.Error.Code == ErrorCodes.Guild.MaintenanceDeferred, progress.Error.Message);
        }

        Assert.Equal(4L, await f.ScalarAsync("SELECT StateCode FROM guild_work_offers"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT Revision FROM guild_work_offers"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_work_offers WHERE ClosedReason='issuer_unavailable'"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests WHERE AssociationKindCode=2"));
    }

    [Fact]
    public async Task Authority_scan_freezes_each_bounded_prefix_and_wraps_despite_new_offers()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (owner, _, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var offers = Enumerable.Range(1, 101).Select(i => (Id: Guid.NewGuid(), Sequence: (long)i)).ToArray();

        await AddAuthorityScanOfferMetadataAsync(f, cohort.Id, members[0].Quest.Id, offers);

        _ = await cohorts.ReconcileAsync(owner, cohort.Id, default);
        _ = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.Equal(100L, await f.ScalarAsync("SELECT LastOfferSequence FROM guild_cohort_award_authority_scan"));

        Assert.Equal(101L, await f.ScalarAsync("SELECT SnapshotSequence FROM guild_cohort_award_authority_scan"));

        Guid late = Guid.NewGuid();

        await AddAuthorityScanOfferMetadataAsync(f, cohort.Id, members[0].Quest.Id, [(late, 102)]);

        Guid key = Guid.NewGuid();

        var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), members[0].Quest.SignetId,
            new(key, members[0].Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);

        _ = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.Equal(4L, await f.ScalarAsync($"SELECT StateCode FROM guild_work_offers WHERE Id='{offers[100].Id:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT StateCode FROM guild_work_offers WHERE Id='{late:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT PhaseCode FROM guild_cohort_award_authority_scan"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT LastOfferSequence FROM guild_cohort_award_authority_scan"));

        _ = await cohorts.ReconcileAsync(owner, cohort.Id, default);
        _ = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.Equal(101L, await f.ScalarAsync("SELECT count(*) FROM guild_work_offers WHERE StateCode=4"));

        _ = await cohorts.ReconcileAsync(owner, cohort.Id, default);

        Assert.Equal(102L, await f.ScalarAsync("SELECT count(*) FROM guild_work_offers WHERE StateCode=4"));
    }

    // This fixture supplies recovery-only issuer metadata; source body hydration has independent tests.
    private static async Task AddAuthorityScanOfferMetadataAsync(Fixture f, Guid cohort, Guid issuer, (Guid Id, long Sequence)[] offers)
    {
        const string requested = "{}";

        string values = string.Join(",", offers.Select(o => $"('{o.Id:D}','{cohort:D}','{issuer:D}',{o.Sequence},'review','{Guid.NewGuid():D}','{requested}','text/plain',1,1)")).ToUpperInvariant().Replace("TEXT/PLAIN", "text/plain");

        await f.ExecuteAsync($"""
            INSERT INTO guild_work_offers(Id,CohortId,IssuerQuestId,OfferSequence,SkillId,GoalContentId,RequestedGrantJson,MimeType,StateCode,Revision)
            VALUES {values};
            UPDATE guild_cohorts SET OfferSequence={offers.Max(o => o.Sequence)},Revision=Revision+1 WHERE Id='{cohort:D}' COLLATE NOCASE;
            """);
    }
}
