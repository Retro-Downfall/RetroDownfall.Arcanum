using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Distribution_events_follow_frozen_offer_and_exact_member_visibility()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        var award = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

        Assert.True(award.IsSuccess, award.Error.Message);

        var cohort = await setup.Store.GetAsync(setup.IssuerCaller, setup.Offer.CohortId, default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        AppendSharedNoteRequest note = new(Guid.NewGuid(), cohort.Value.Revision, GuildNoteKind.Observation, null, "shared finding", null);

        var appended = await setup.Store.AppendNoteAsync(setup.IssuerCaller, NoteMutation(note), cohort.Value.Id, note, default);

        Assert.True(appended.IsSuccess, appended.Error.Message);

        var issuer = await f.Store.GetAsync(setup.Owner, setup.Issuer.Quest.Id, default);
        cohort = await setup.Store.GetAsync(setup.IssuerCaller, setup.Offer.CohortId, default);

        Assert.True(issuer.IsSuccess, issuer.Error.Message);
        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid key = Guid.NewGuid();

        var late = await f.Store.IssueCohortMemberAsync(setup.IssuerCaller,
            new(f.Generation, setup.Issuer.Quest.Id, null, key, key), cohort.Value.Id, cohort.Value.Revision,
            new(key, issuer.Value.Quest.Revision, null, "Late member", "Review", new()), default);

        Assert.True(late.IsSuccess, late.Error.Message);

        var currentPrimary = await f.Store.GetAsync(setup.Owner, setup.Primary.Quest.Id, default);

        Assert.True(currentPrimary.IsSuccess, currentPrimary.Error.Message);

        GuildCallerContext winner = await BindCohortRunAsync(f, setup.Owner, currentPrimary.Value);
        GuildCallerContext lateRun = await BindCohortRunAsync(f, setup.Owner, late.Value);

        GuildPortalStore portal = new(f.Store, f.Authority, new EventDisclosurePolicy(f.Policy), TimeProvider.System);

        GuildEventQuery query = new(setup.Issuer.Quest.RootQuestId, null, cohort.Value.Id, null, 100);

        var operatorEvents = await portal.ReadEventsAsync(setup.Owner, query, default);
        var winnerEvents = await portal.ReadEventsAsync(winner, query, default);
        var lateEvents = await portal.ReadEventsAsync(lateRun, query, default);

        Assert.True(operatorEvents.IsSuccess, operatorEvents.Error.Message);
        Assert.True(winnerEvents.IsSuccess, winnerEvents.Error.Message);
        Assert.True(lateEvents.IsSuccess, lateEvents.Error.Message);

        foreach (GuildEventPage page in new[] { operatorEvents.Value, winnerEvents.Value })
        {
            Assert.Contains(page.Items, item => item.EventKind == GuildEventKind.OfferChanged && item.ObjectId == setup.Offer.Id);
            Assert.Contains(page.Items, item => item.EventKind == GuildEventKind.BidChanged && item.ObjectId == setup.Bid.Id);
            Assert.Contains(page.Items, item => item.EventKind == GuildEventKind.AwardCommitted && item.ObjectId == award.Value.Id);
            Assert.Contains(page.Items, item => item.EventKind == GuildEventKind.NoteAppended && item.ObjectId == appended.Value.Id);
        }

        Assert.DoesNotContain(lateEvents.Value.Items, item => item.ObjectId == setup.Offer.Id || item.ObjectId == setup.Bid.Id || item.ObjectId == award.Value.Id);

        Assert.Contains(lateEvents.Value.Items, item => item.EventKind == GuildEventKind.NoteAppended && item.ObjectId == appended.Value.Id);
    }

    private sealed class EventDisclosurePolicy(GuildLocalHostPolicy inner) : IGuildQuestHostPolicy, IGuildPortalDisclosurePolicy
    {
        public Guid InstallationGeneration => inner.InstallationGeneration;

        public string VisibilityEpoch => "distribution-event-test-v1";

        public Task<Result> AuthorizeLocateAsync(GuildCallerContext caller, SqliteConnection connection, SqliteTransaction transaction,
            Guid? questId, Guid? sessionId, Guid? campaignId, CancellationToken cancellationToken) =>
            inner.AuthorizeLocateAsync(caller, connection, transaction, questId, sessionId, campaignId, cancellationToken);

        public Task<Result<GuildQuestAdmissionPlan>> ResolveAsync(GuildCallerContext caller, GuildQuestProposal proposal,
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
            inner.ResolveAsync(caller, proposal, connection, transaction, cancellationToken);

        public Task<Result<ArtifactSensitivityLabel?>> ResolvePublicationSensitivityAsync(GuildCallerContext caller, GuildQuestStored source,
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
            Task.FromResult(Result<ArtifactSensitivityLabel?>.Success(null));

        public Task<Result> AuthorizeContentAsync(GuildCallerContext caller, ArtifactSensitivityLabel label,
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
