using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Foreign_award_and_withdraw_do_not_reveal_protected_offer_content()
    {
        await using Fixture f = await Fixture.CreateAsync();
        var target = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);
        Guid offerId = await SeedMarketReadOfferAsync(f, target.Cohort.Id, target.Root, target.Members[0], 1);
        var offer = await target.Cohorts.GetOfferAsync(target.Issuer, offerId, default);
        Assert.True(offer.IsSuccess, offer.Error.Message);

        await using (var connection = await f.OpenAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var reference = offer.Value.GoalRef;
            var label = new DerivedArtifactWrite(SensitiveArtifactKind.GuildContent, reference.ContentId,
                target.Root.Quest.SessionId, target.Root.Quest.CampaignId, null, (ulong)reference.Revision,
                new CovenantDigest(Convert.FromHexString(reference.Digest)), ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()]));
            var marked = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, label, default);
            Assert.True(marked.IsSuccess, marked.Error.Message);
            await transaction.CommitAsync();
        }

        var foreign = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 0);
        GuildRevisionRequest withdraw = new(Guid.NewGuid(), 1);
        string withdrawDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(withdraw, GuildJsonContext.Default.GuildRevisionRequest)));
        var deniedWithdraw = await foreign.Cohorts.WithdrawOfferAsync(foreign.Issuer,
            new(withdraw.IdempotencyKey, withdrawDigest), offerId, withdraw, default);
        Assert.Equal(ErrorCodes.Guild.NotFound, deniedWithdraw.Error.Code);

        AwardWorkRequest award = new(Guid.NewGuid(), 1, 1, Guid.NewGuid());
        string awardDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(award, GuildJsonContext.Default.AwardWorkRequest)));
        var deniedAward = await foreign.Cohorts.AwardAsync(foreign.Issuer,
            new(award.IdempotencyKey, awardDigest), offerId, award, default);
        Assert.Equal(ErrorCodes.Guild.NotFound, deniedAward.Error.Code);
    }
}
