using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Market_offer_reads_require_the_frozen_recipient_and_current_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        Guid offer = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 1);

        var found = await cohorts.GetOfferAsync(issuer, offer, default);

        Assert.True(found.IsSuccess, found.Error.Message);

        Assert.Equal(offer, found.Value.Id);

        Assert.True((await cohorts.GetOfferAsync(owner, offer, default)).IsSuccess);

        var recipient = await BindCohortRunAsync(f, owner, members[0]);

        Assert.True((await cohorts.GetOfferAsync(recipient, offer, default)).IsSuccess);

        var excluded = await BindCohortRunAsync(f, owner, members[1]);

        var refused = await cohorts.GetOfferAsync(excluded, offer, default);

        Assert.Equal(ErrorCodes.Guild.NotFound, refused.Error.Code);

        await f.ExecuteAsync($"UPDATE guild_signets SET Revoked=1,Revision=Revision+1,RevocationGeneration=RevocationGeneration+1 WHERE QuestId='{members[0].Quest.Id:D}' COLLATE NOCASE");

        Assert.True((await cohorts.GetOfferAsync(recipient, offer, default)).IsFailure);
    }

    [Fact]
    public async Task Market_offer_pages_pin_sequence_and_reject_foreign_cursor_scope()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        Guid firstId = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 1);

        Guid secondId = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 2);

        var first = await cohorts.ListOffersAsync(issuer, cohort.Id, null, new(PageSize: 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(firstId, Assert.Single(first.Value.Items).Id);

        Assert.NotNull(first.Value.NextCursor);

        await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 3);

        var second = await cohorts.ListOffersAsync(issuer, cohort.Id, null, new(first.Value.NextCursor, 1), default);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(secondId, Assert.Single(second.Value.Items).Id);

        Assert.Equal(first.Value.SnapshotSequence, second.Value.SnapshotSequence);

        Assert.Null(second.Value.NextCursor);

        var mismatch = await cohorts.ListOffersAsync(owner, cohort.Id, null, new(first.Value.NextCursor, 1), default);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, mismatch.Error.Code);
    }

    [Fact]
    public async Task Market_offer_pages_skip_protected_content_and_keep_a_progressing_cursor()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (_, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        Guid firstId = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 1);

        Guid secondId = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 2);

        var found = await cohorts.GetOfferAsync(issuer, firstId, default);

        Assert.True(found.IsSuccess, found.Error.Message);

        var reference = found.Value.GoalRef;

        await using (var connection = await f.OpenAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var label = new DerivedArtifactWrite(SensitiveArtifactKind.GuildContent, reference.ContentId,
                root.Quest.SessionId, root.Quest.CampaignId, null, (ulong)reference.Revision,
                new CovenantDigest(Convert.FromHexString(reference.Digest)), ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()]));

            var marked = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, label, default);

            Assert.True(marked.IsSuccess, marked.Error.Message);

            await transaction.CommitAsync();
        }

        Assert.True((await cohorts.GetOfferAsync(issuer, firstId, default)).IsFailure);

        var first = await cohorts.ListOffersAsync(issuer, cohort.Id, null, new(PageSize: 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Empty(first.Value.Items);

        Assert.NotNull(first.Value.NextCursor);

        var next = await cohorts.ListOffersAsync(issuer, cohort.Id, null, new(first.Value.NextCursor, 1), default);

        Assert.True(next.IsSuccess, next.Error.Message);

        Assert.Equal(secondId, Assert.Single(next.Value.Items).Id);

        Assert.Null(next.Value.NextCursor);
    }

    [Fact]
    public async Task Market_offer_content_erasure_preserves_only_the_exact_reference_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (_, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        Guid offer = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 1);

        var original = await cohorts.GetOfferAsync(issuer, offer, default);

        Assert.True(original.IsSuccess, original.Error.Message);

        await using (var connection = await f.OpenAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction,
                original.Value.GoalRef.ContentId.ToString("N"), default);

            await using var delete = connection.CreateCommand();

            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";
            delete.Parameters.AddWithValue("$id", original.Value.GoalRef.ContentId.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await delete.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        var erased = await cohorts.GetOfferAsync(issuer, offer, default);

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.Equal(original.Value.GoalRef with { ContentErased = true }, erased.Value.GoalRef);
    }

    [Fact]
    public async Task Market_bid_empty_advisory_note_roundtrips_without_allocating_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        Guid offer = await SeedMarketReadOfferAsync(f, cohort.Id, root, members[0], 1);

        GuildCallerContext bidder = await BindCohortRunAsync(f, owner, members[0]);

        SubmitBidRequest request = new(Guid.NewGuid(), 1, "", null);

        GuildMutationIdentity mutation = new(request.IdempotencyKey,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.SubmitBidRequest))));

        long questsBefore = await f.ScalarAsync("SELECT count(*) FROM guild_quests");

        var bid = await cohorts.SubmitBidAsync(bidder.Execution!, mutation, offer, request, default);

        Assert.True(bid.IsSuccess, bid.Error.Message);

        Assert.Equal(0, bid.Value.NoteRef.ByteLength);

        var page = await cohorts.ListBidsAsync(issuer, offer, new(), default);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Equal(bid.Value.Id, Assert.Single(page.Value.Items).Id);

        Assert.Equal(questsBefore, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }

    private static async Task<Guid> SeedMarketReadOfferAsync(Fixture f, Guid cohort, GuildQuestStored issuer, GuildQuestStored recipient, long sequence)
    {
        Guid offer = Guid.NewGuid();

        Guid content = Guid.NewGuid();

        byte[] body = "Perform the offered work"u8.ToArray();

        GuildStore store = new(f.Connections, f.Gate, TimeProvider.System);

        var written = await store.WriteContentAsync(new(content, offer, GuildContentKind.Offer, 1,
            issuer.Quest.SessionId, issuer.Quest.CampaignId, "text/plain", body,
            new DerivedArtifactWrite(SensitiveArtifactKind.GuildContent, content, issuer.Quest.SessionId,
                issuer.Quest.CampaignId, null, 1, new CovenantDigest(SHA256.HashData(body)),
                ContentSensitivity.None, GenerationProvenance.CreateExact([]))), default);

        Assert.True(written.IsSuccess, written.Error.Message);

        string O(Guid value) => value.ToString("D").ToUpperInvariant();

        string requestedGrant = "{}";

        await f.ExecuteAsync($"""
            UPDATE guild_cohorts SET OfferSequence={sequence},Revision=Revision+1 WHERE Id='{O(cohort)}';
            INSERT INTO guild_work_offers(Id,CohortId,IssuerQuestId,OfferSequence,SkillId,GoalContentId,RequestedGrantJson,MimeType,StateCode,Revision)
            VALUES('{O(offer)}','{O(cohort)}','{O(issuer.Quest.Id)}',{sequence},'review','{O(content)}','{requestedGrant}','text/plain',1,1);
            INSERT INTO guild_work_offer_recipients(OfferId,MemberId,AgentId,PrimaryQuestId,CardId,CardRevision)
            SELECT '{O(offer)}',Id,AgentId,PrimaryVotingQuestId,'{O(recipient.Quest.CardId)}',{recipient.Quest.CardRevision}
                FROM guild_cohort_members WHERE CohortId='{O(cohort)}' AND PrimaryVotingQuestId='{O(recipient.Quest.Id)}';
            """);

        return offer;
    }
}
