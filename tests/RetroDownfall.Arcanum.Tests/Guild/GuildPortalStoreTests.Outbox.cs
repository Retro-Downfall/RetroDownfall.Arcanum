using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Source_outbox_retries_after_failure_and_restart_without_reexecuting_child_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestResult result = PortalResult(root.Quest.Id, "retained result");

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest),
            root.Quest.Revision, result, default)).IsSuccess);

        await f.ExecuteAsync("CREATE TRIGGER fixture_outbox_failure BEFORE INSERT ON guild_envelopes BEGIN SELECT RAISE(ABORT,'fixture storage failure'); END");

        Assert.True((await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default)).IsFailure);

        Assert.Equal(3L, await PortalScalarAsync(f, "SELECT count(*) FROM guild_portal_outbox WHERE DeliveryStateCode=1"));

        Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));

        await f.ExecuteAsync("DROP TRIGGER fixture_outbox_failure");

        f.Restart();

        Result<int> drained = await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Assert.Equal(3, drained.Value);

        Assert.Equal(0, (await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default)).Value);

        Assert.Equal(3L, await f.CountAsync("guild_envelopes"));

        Assert.Equal(0L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(1L, await f.CountAsync("fixture_settlements"));
    }

    [Fact]
    public async Task Operator_visibility_receipt_never_creates_a_consumer_input_or_fabricated_run()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildMessageReceipt message = await PublishHostNoticeAsync(f, root, GuildEnvelopeKind.QuestIssued);

        Result<GuildCircleAcknowledgement> acknowledgement = await f.Portal.AcknowledgeOperatorAsync(f.Caller, message.MessageId, default);

        Assert.True(acknowledgement.IsSuccess, acknowledgement.Error.Message);

        Assert.Equal(0L, await f.CountAsync("guild_portal_inputs"));

        Assert.Equal(1L, await PortalScalarAsync(f, "SELECT count(*) FROM guild_portal_receipts WHERE EffectKindCode=2"));

        f.Restart();

        Result<GuildCircleAcknowledgement> replay = await f.Portal.AcknowledgeOperatorAsync(f.Caller, message.MessageId, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(acknowledgement.Value.ApplicationReceiptId, replay.Value.ApplicationReceiptId);

        Assert.True(replay.Value.Receipt.Replayed);
    }

    [Fact]
    public async Task Result_envelope_application_and_domain_parent_input_commit_together()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parentDriver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, parentDriver);

        GuildQuestDriver childDriver = await f.DriverAsync(child);

        GuildQuestResult result = PortalResult(child.Quest.Id, "child result");

        Assert.True((await f.Store.CommitResultAsync(childDriver, new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest),
            child.Quest.Revision, result, default)).IsSuccess);

        child = (await f.Store.GetAsync(f.Caller, child.Quest.Id, default)).Value;

        _ = await PublishHostNoticeAsync(f, child, GuildEnvelopeKind.Result);

        GuildPortalClaim claim = (await f.Portal.ClaimAsync(parentDriver, default)).Value;

        await f.ExecuteAsync("CREATE TRIGGER fixture_result_ack_failure BEFORE UPDATE ON guild_inbox BEGIN SELECT RAISE(ABORT,'ack failure'); END");

        Assert.True((await f.Portal.ApplyAsync(claim, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_result_applications"));

        Assert.Equal(0L, await f.CountAsync("guild_result_inputs"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_receipts"));

        await f.ExecuteAsync("DROP TRIGGER fixture_result_ack_failure");

        Assert.True((await f.Portal.ApplyAsync(claim, default)).IsSuccess);

        Assert.True((await f.Portal.ApplyAsync(claim, default)).Value.Receipt.Replayed);

        Assert.Equal(1L, await f.CountAsync("guild_result_inputs"));

        Assert.Equal(1L, await f.CountAsync("guild_portal_inputs"));
    }

    [Fact]
    public async Task Effect_projection_failure_rolls_back_input_receipt_and_inbox_ack()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        _ = (await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "input"), default)).Value;

        GuildPortalClaim claim = (await f.Portal.ClaimAsync(driver, default)).Value;

        await f.ExecuteAsync("CREATE TRIGGER fixture_effect_event_failure BEFORE INSERT ON guild_events BEGIN SELECT RAISE(ABORT,'effect event failure'); END");

        Assert.True((await f.Portal.ApplyAsync(claim, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_portal_inputs"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_receipts"));
    }

    [Fact]
    public async Task Source_outbox_withholds_a_closed_destination_without_blocking_other_receipts()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parent = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, parent);

        GuildQuestDriver childDriver = await f.DriverAsync(child);

        GuildQuestResult childResult = PortalResult(child.Quest.Id, "child result");

        Assert.True((await f.Store.CommitResultAsync(childDriver, new(Guid.NewGuid(), childResult.Receipt.CanonicalContentDigest),
            child.Quest.Revision, childResult, default)).IsSuccess);

        GuildQuestResult rootResult = PortalResult(root.Quest.Id, "parent complete");

        Assert.True((await f.Store.CommitResultAsync(parent, new(Guid.NewGuid(), rootResult.Receipt.CanonicalContentDigest),
            root.Quest.Revision, rootResult, default)).IsSuccess);

        Result<int> drained = await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Assert.Equal(6, drained.Value);

        Assert.Equal(3L, await PortalScalarAsync(f, "SELECT count(*) FROM guild_portal_outbox WHERE DeliveryStateCode=2"));

        Assert.Equal(3L, await PortalScalarAsync(f, "SELECT count(*) FROM guild_portal_outbox WHERE DeliveryStateCode=3"));

        Assert.Equal(1L, await PortalScalarAsync(f, "SELECT count(*) FROM guild_result_outbox WHERE RecipientQuestId IS NOT NULL AND DeliveryStateCode=3"));

        Assert.Equal(0, (await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default)).Value);

        Assert.Equal(3L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Operator_result_visibility_and_domain_delivery_are_one_replayable_transaction()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestResult result = PortalResult(root.Quest.Id, "root result");

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest),
            root.Quest.Revision, result, default)).IsSuccess);

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        GuildMessageReceipt message = await PublishHostNoticeAsync(f, root, GuildEnvelopeKind.Result);

        await f.ExecuteAsync("CREATE TRIGGER fixture_operator_event_failure BEFORE INSERT ON guild_events BEGIN SELECT RAISE(ABORT,'event failure'); END");

        Assert.True((await f.Portal.AcknowledgeOperatorAsync(f.Caller, message.MessageId, default)).IsFailure);

        Assert.Equal(1L, await PortalScalarAsync(f, "SELECT count(*) FROM guild_result_outbox WHERE DeliveryStateCode=1"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_receipts"));

        await f.ExecuteAsync("DROP TRIGGER fixture_operator_event_failure");

        Result<GuildCircleAcknowledgement> accepted = await f.Portal.AcknowledgeOperatorAsync(f.Caller, message.MessageId, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.Equal(1L, await PortalScalarAsync(f, "SELECT count(*) FROM guild_result_outbox WHERE DeliveryStateCode=2"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_inputs"));

        f.Restart();

        Assert.Equal(accepted.Value.ApplicationReceiptId,
            (await f.Portal.AcknowledgeOperatorAsync(f.Caller, message.MessageId, default)).Value.ApplicationReceiptId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_root_result_acknowledgement_projects_the_source_transaction_once(bool resultUsesQuestIdentity)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestResult result = PortalResult(root.Quest.Id, "root result");

        if (resultUsesQuestIdentity) result = result with { Receipt = result.Receipt with { ResultId = root.Quest.Id } };

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest),
            root.Quest.Revision, result, default)).IsSuccess);

        long before = await f.CountAsync("guild_events");

        await f.ExecuteAsync("CREATE TRIGGER fixture_direct_ack_event_failure BEFORE INSERT ON guild_events BEGIN SELECT RAISE(ABORT,'event failure'); END");

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, root.Quest.Id, result.Receipt.ResultId,
            result.Receipt.CanonicalContentDigest, 1, default)).IsFailure);

        Assert.Equal(1L, await PortalScalarAsync(f, "SELECT count(*) FROM guild_result_outbox WHERE DeliveryStateCode=1"));

        await f.ExecuteAsync("DROP TRIGGER fixture_direct_ack_event_failure");

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, root.Quest.Id, result.Receipt.ResultId,
            result.Receipt.CanonicalContentDigest, 1, default)).IsSuccess);

        Assert.Equal(before + 1, await f.CountAsync("guild_events"));

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, root.Quest.Id, result.Receipt.ResultId,
            result.Receipt.CanonicalContentDigest, 1, default)).IsSuccess);

        Assert.Equal(before + 1, await f.CountAsync("guild_events"));
    }

    private static GuildQuestResult PortalResult(Guid quest, string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);

        string digest = Convert.ToHexString(SHA256.HashData(body));

        return new(new(quest, Guid.NewGuid(), digest, new(Guid.NewGuid(), GuildContentKind.QuestResult, 1,
            digest, "text/plain", body.Length, false), QuestState.Completed, null), body, NoDispatchProven: true, WorkStoppedProven: true);
    }

    private static async Task<GuildMessageReceipt> PublishHostNoticeAsync(Fixture f, GuildQuestStored source, GuildEnvelopeKind kind)
    {
        Guid receipt = kind == GuildEnvelopeKind.QuestIssued ? source.Quest.Id : source.Quest.ResultReceiptId!.Value;

        string sourceKind = kind == GuildEnvelopeKind.QuestIssued ? "questIssue" : kind == GuildEnvelopeKind.Result ? "result" : "settlement";

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, source.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: receipt, receiptSource: sourceKind);

        var notice = await f.Authority.InQuestTransactionAsync<(string MimeType, string Body)>(source.Quest.RootQuestId,
            (c, t, _, ct) => GuildPortalStore.ReadHostNoticeWithinAsync(c, t, source, kind, ct), default);

        Assert.True(notice.IsSuccess, notice.Error.Message);

        GuildCircleAddress address = source.Quest.ParentQuestId is { } parent
            ? new(GuildCircleAddressKind.Parent, parent, null, null) : new(GuildCircleAddressKind.Operator, null, null, null);

        Result<GuildMessageReceipt> published = await f.Portal.PublishAsync(caller, source.Quest.Id,
            new(receipt, address, kind, notice.Value.MimeType, notice.Value.Body, null), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        return published.Value;
    }

    private static async Task<long> PortalScalarAsync(Fixture f, string sql)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
