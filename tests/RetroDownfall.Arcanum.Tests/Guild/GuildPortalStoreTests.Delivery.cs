using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Ordered_input_application_and_lost_ack_commit_one_durable_effect()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildMessageReceipt first = (await f.Portal.PublishAsync(f.Caller, root.Quest.Id,
            Intervention(root.Quest.Id, "first"), default)).Value;

        GuildMessageReceipt second = (await f.Portal.PublishAsync(f.Caller, root.Quest.Id,
            Intervention(root.Quest.Id, "second"), default)).Value;

        Result<GuildPortalClaim> claimed = await f.Portal.ClaimAsync(driver, default);

        Assert.True(claimed.IsSuccess, claimed.Error.Message);

        Assert.Equal(first.MessageId, claimed.Value.EnvelopeId);

        Assert.Equal("first", Encoding.UTF8.GetString((await f.Portal.ReadClaimAsync(claimed.Value, default)).Value));

        Result<GuildCircleAcknowledgement> applied = await f.Portal.ApplyAsync(claimed.Value, default);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Result<GuildCircleAcknowledgement> replay = await f.Portal.ApplyAsync(claimed.Value, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(applied.Value.ApplicationReceiptId, replay.Value.ApplicationReceiptId);

        Assert.True(replay.Value.Receipt.Replayed);

        Assert.Equal(1L, await f.CountAsync("guild_portal_inputs"));

        Assert.Equal(second.MessageId, (await f.Portal.ClaimAsync(driver, default)).Value.EnvelopeId);
    }

    [Fact]
    public async Task Replaced_lease_cannot_read_or_apply_an_old_claim()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        Assert.True((await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "private"), default)).IsSuccess);

        GuildPortalClaim claim = (await f.Portal.ClaimAsync(driver, default)).Value;

        await f.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner='replacement',Revision=Revision+1");

        Assert.True((await f.Portal.ReadClaimAsync(claim, default)).IsFailure);

        Assert.True((await f.Portal.ApplyAsync(claim, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_portal_inputs"));
    }

    [Fact]
    public async Task Failed_consumer_input_rolls_back_receipt_and_acknowledgement()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        Assert.True((await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "input"), default)).IsSuccess);

        GuildPortalClaim claim = (await f.Portal.ClaimAsync(driver, default)).Value;

        await f.ExecuteAsync("CREATE TRIGGER fixture_refuse_input BEFORE INSERT ON guild_portal_inputs BEGIN SELECT RAISE(ABORT,'fixture input failure'); END");

        Assert.True((await f.Portal.ApplyAsync(claim, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_portal_receipts"));

        await f.ExecuteAsync("DROP TRIGGER fixture_refuse_input");

        Assert.True((await f.Portal.ApplyAsync(claim, default)).IsSuccess);

        Assert.Equal(1L, await f.CountAsync("guild_portal_inputs"));
    }

    [Fact]
    public async Task Expired_claim_redelivers_after_restart_to_the_exact_new_holder()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver oldDriver = await f.DriverAsync(root);

        GuildMessageReceipt message = (await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "retry"), default)).Value;

        GuildPortalClaim oldClaim = (await f.Portal.ClaimAsync(oldDriver, default)).Value;

        await f.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt='2000-01-01T00:00:00.0000000Z'");

        f.Restart();

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        GuildQuestDriver newDriver = await f.DriverAsync(root, "replacement");

        GuildPortalClaim replacement = (await f.Portal.ClaimAsync(newDriver, default)).Value;

        Assert.Equal(message.MessageId, replacement.EnvelopeId);

        Assert.True(replacement.InboxRevision > oldClaim.InboxRevision);

        Assert.True((await f.Portal.ApplyAsync(oldClaim, default)).IsFailure);

        Assert.True((await f.Portal.ApplyAsync(replacement, default)).IsSuccess);

        Assert.Equal(1L, await f.CountAsync("guild_portal_inputs"));
    }

    [Fact]
    public async Task Concurrent_acknowledgements_return_one_committed_application()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        _ = (await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "one input"), default)).Value;

        GuildPortalClaim claim = (await f.Portal.ClaimAsync(driver, default)).Value;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => f.Portal.ApplyAsync(claim, default)));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error.Message));

        Assert.Single(results.Select(result => result.Value.ApplicationReceiptId).Distinct());

        Assert.Equal(1L, await f.CountAsync("guild_portal_inputs"));
    }

    [Fact]
    public async Task An_outstanding_claim_blocks_its_stream_while_another_producer_can_progress()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parent = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, parent);

        GuildQuestDriver childDriver = await f.DriverAsync(child);

        Guid first = (await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "first"), default)).Value.MessageId;

        Guid second = (await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "second"), default)).Value.MessageId;

        Guid independent = (await f.Portal.PublishAsync(f.RunCaller(childDriver), child.Quest.Id,
            new(Guid.NewGuid(), new(GuildCircleAddressKind.Parent, root.Quest.Id, null, null), GuildEnvelopeKind.Progress,
                "text/plain", "independent", null), default)).Value.MessageId;

        GuildPortalClaim held = (await f.Portal.ClaimAsync(parent, default)).Value;

        Assert.Equal(first, held.EnvelopeId);

        GuildPortalClaim other = (await f.Portal.ClaimAsync(parent, default)).Value;

        Assert.Equal(independent, other.EnvelopeId);

        Assert.True((await f.Portal.ClaimAsync(parent, default)).IsFailure);

        Assert.True((await f.Portal.ApplyAsync(other, default)).IsSuccess);

        Assert.True((await f.Portal.ApplyAsync(held, default)).IsSuccess);

        Assert.Equal(second, (await f.Portal.ClaimAsync(parent, default)).Value.EnvelopeId);
    }

    private static GuildPublishRequest Intervention(Guid quest, string body) =>
        new(Guid.NewGuid(), new(GuildCircleAddressKind.Child, quest, null, null), GuildEnvelopeKind.Intervention, "text/plain", body, null);
}
