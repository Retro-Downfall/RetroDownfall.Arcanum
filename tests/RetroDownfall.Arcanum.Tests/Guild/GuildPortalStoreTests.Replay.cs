using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Source_invalidation_identifies_its_owning_Quest_separately_from_the_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildPortalReplayCursor cursor = (await f.Portal.CaptureReplayCursorAsync(f.Caller, root.Quest.Id, default)).Value;

        Guid source = Guid.NewGuid();

        Assert.True((await f.Authority.InQuestTransactionAsync<long>(root.Quest.Id,
            (connection, transaction, _, token) => GuildPortalEvents.AppendWithinAsync(connection, transaction,
                root.Quest.Id, root.Quest.Id, GuildEventKind.QuestChanged, source, 7, null, DateTimeOffset.UtcNow, token), default)).IsSuccess);

        GuildChangeEventDto item = Assert.Single((await f.Portal.ReadReplayAsync(f.Caller, root.Quest.Id, cursor.Cursor, default)).Value.Page.Items);

        Assert.Equal(source, item.ObjectId);

        Assert.Equal(7, item.Revision);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(item, GuildJsonContext.Default.GuildChangeEventDto));

        Assert.True(json.RootElement.TryGetProperty("questId", out JsonElement owner), "An invalidation must identify the owning Quest independently of its receipt.");

        Assert.Equal(root.Quest.Id, owner.GetGuid());
    }

    [Fact]
    public async Task Snapshot_pages_exclude_later_events_and_replay_handoff_observes_them_after_restart()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await AppendReplayEventsAsync(f, root.Quest.Id, root.Quest.Id, 101);

        Result<GuildEventPage> first = await f.Portal.ReadEventsAsync(f.Caller, new(root.Quest.Id, null, null, null), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(100, first.Value.Items.Length);

        Assert.NotNull(first.Value.NextCursor);

        Result<GuildPortalReplayCursor> captured = await f.Portal.CaptureReplayCursorAsync(f.Caller, root.Quest.Id, default);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Assert.Equal(first.Value.SnapshotSequence, captured.Value.Watermark);

        await AppendReplayEventsAsync(f, root.Quest.Id, root.Quest.Id, 3);

        f.Restart();

        Result<GuildEventPage> second = await f.Portal.ReadEventsAsync(f.Caller, new(root.Quest.Id, null, null, first.Value.NextCursor), default);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.All(second.Value.Items, item => Assert.True(item.EventSequence <= captured.Value.Watermark));

        Assert.Null(second.Value.NextCursor);

        Result<GuildPortalReplayPage> replay = await f.Portal.ReadReplayAsync(f.Caller, root.Quest.Id, captured.Value.Cursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(3, replay.Value.Page.Items.Length);

        Assert.True(replay.Value.CaughtUp);

        Assert.NotEqual(captured.Value.Cursor, replay.Value.ResumeCursor);

        Result<GuildPortalReplayPage> empty = await f.Portal.ReadReplayAsync(f.Caller, root.Quest.Id, replay.Value.ResumeCursor, default);

        Assert.True(empty.IsSuccess, empty.Error.Message);

        Assert.Empty(empty.Value.Page.Items);

        Assert.True(empty.Value.CaughtUp);

        Assert.NotEqual(replay.Value.ResumeCursor, empty.Value.ResumeCursor);
    }

    [Fact]
    public async Task Cursor_cannot_cross_principal_root_filter_or_route_and_epoch_changes_require_resync()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await AppendReplayEventsAsync(f, root.Quest.Id, root.Quest.Id, 2);

        Result<GuildEventPage> page = await f.Portal.ReadEventsAsync(f.Caller, new(root.Quest.Id, null, null, null, 1), default);

        Assert.True(page.IsSuccess, page.Error.Message);

        string cursor = page.Value.NextCursor!;

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await f.Portal.ReadEventsAsync(f.Caller, new(root.Quest.Id, root.Quest.Id, null, cursor), default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await f.Portal.ReadEventsAsync(f.Caller, new(Guid.NewGuid(), null, null, cursor), default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await f.Portal.ReadReplayAsync(f.Caller, root.Quest.Id, cursor, default)).Error.Code);

        GuildCallerContext other = new(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), f.Host.InstallationGeneration);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await f.Portal.ReadEventsAsync(other, new(root.Quest.Id, null, null, cursor), default)).Error.Code);

        f.Host.VisibilityEpoch = "policy-changed";

        Assert.Equal(ErrorCodes.Guild.CursorGone, (await f.Portal.ReadEventsAsync(f.Caller, new(root.Quest.Id, null, null, cursor), default)).Error.Code);
    }

    [Fact]
    public async Task Replay_detects_missing_retained_event_rows_and_authority_reset_instead_of_silent_loss()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Result<GuildPortalReplayCursor> captured = await f.Portal.CaptureReplayCursorAsync(f.Caller, root.Quest.Id, default);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        await AppendReplayEventsAsync(f, root.Quest.Id, root.Quest.Id, 3);

        await f.ExecuteAsync($"DELETE FROM guild_events WHERE EventSequence={captured.Value.Watermark + 2}");

        Assert.Equal(ErrorCodes.Guild.CursorGone, (await f.Portal.ReadReplayAsync(f.Caller, root.Quest.Id, captured.Value.Cursor, default)).Error.Code);

        await f.ExecuteAsync("UPDATE guild_signet_authority_state SET Epoch='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'");

        Assert.Equal(ErrorCodes.Guild.CursorGone, (await f.Portal.ReadReplayAsync(f.Caller, root.Quest.Id, captured.Value.Cursor, default)).Error.Code);
    }

    [Fact]
    public async Task Same_root_sibling_events_are_filtered_while_scanned_sequences_advance()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored first = await f.ChildAsync(root, driver);

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        GuildQuestStored second = await f.ChildAsync(root, driver);

        GuildQuestDriver firstDriver = await f.DriverAsync(first);

        GuildCallerContext caller = f.RunCaller(firstDriver);

        Result<GuildPortalReplayCursor> captured = await f.Portal.CaptureReplayCursorAsync(caller, root.Quest.Id, default);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        await AppendReplayEventsAsync(f, root.Quest.Id, second.Quest.Id, 101);

        Result<GuildPortalReplayPage> filtered = await f.Portal.ReadReplayAsync(caller, root.Quest.Id, captured.Value.Cursor, default);

        Assert.True(filtered.IsSuccess, filtered.Error.Message);

        Assert.Empty(filtered.Value.Page.Items);

        Assert.False(filtered.Value.CaughtUp);

        Assert.NotEqual(captured.Value.Cursor, filtered.Value.ResumeCursor);

        Result<GuildPortalReplayPage> last = await f.Portal.ReadReplayAsync(caller, root.Quest.Id, filtered.Value.ResumeCursor, default);

        Assert.True(last.IsSuccess, last.Error.Message);

        Assert.Empty(last.Value.Page.Items);

        Assert.True(last.Value.CaughtUp);
    }

    [Fact]
    public async Task Inbox_metadata_reports_withholding_without_disclosing_a_body_reference()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Result<GuildMessageReceipt> published = await f.Portal.PublishAsync(f.Caller, root.Quest.Id,
            Progress() with { Kind = GuildEnvelopeKind.Intervention, Address = new(GuildCircleAddressKind.Child, root.Quest.Id, null, null) }, default);

        Assert.True(published.IsSuccess, published.Error.Message);

        await f.ExecuteAsync("UPDATE guild_inbox SET DeliveryStateCode=3,Revision=Revision+1,ReasonCode='OwnerUnavailable',AcknowledgedAtUtc='2026-11-06T00:00:00.0000000Z'");

        Result<GuildCircleInboxPage> result = await f.Portal.ReadInboxAsync(f.Caller, new(root.Quest.Id, null), default);

        Assert.True(result.IsSuccess, result.Error.Message);

        GuildCircleInboxItem item = Assert.Single(result.Value.Items);

        Assert.Null(item.Envelope);

        string encoded = JsonSerializer.Serialize(item, GuildJsonContext.Default.GuildCircleInboxItem);

        Assert.Contains("\"deliveryState\":\"withheld\"", encoded, StringComparison.Ordinal);

        Assert.Contains("\"reasonCode\":\"OwnerUnavailable\"", encoded, StringComparison.Ordinal);

        Assert.DoesNotContain("contentRef", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inbox_membership_uses_admission_events_once_after_a_delivery_revision_event()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Result<GuildMessageReceipt> published = await f.Portal.PublishAsync(f.Caller, root.Quest.Id,
            Progress() with { Kind = GuildEnvelopeKind.Intervention, Address = new(GuildCircleAddressKind.Child, root.Quest.Id, null, null) }, default);

        Assert.True(published.IsSuccess, published.Error.Message);

        Result<long> effect = await f.Authority.InQuestTransactionAsync<long>(root.Quest.Id, (connection, transaction, _, ct) =>
            GuildPortalEvents.AppendWithinAsync(connection, transaction, root.Quest.Id, root.Quest.Id, GuildEventKind.MessageCommitted,
                Guid.NewGuid(), 2, published.Value.MessageId, DateTimeOffset.UtcNow, ct), default);

        Assert.True(effect.IsSuccess, effect.Error.Message);

        Result<GuildCircleInboxPage> page = await f.Portal.ReadInboxAsync(f.Caller, new(root.Quest.Id, null), default);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Single(page.Value.Items);
    }

    [Fact]
    public async Task A_parent_message_addressed_to_a_sibling_does_not_expose_an_event_to_another_child()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parentDriver = await f.DriverAsync(root);

        GuildQuestStored first = await f.ChildAsync(root, parentDriver);

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        GuildQuestStored second = await f.ChildAsync(root, parentDriver);

        GuildQuestDriver firstDriver = await f.DriverAsync(first);

        GuildCallerContext reader = f.RunCaller(firstDriver);

        Result<GuildPortalReplayCursor> captured = await f.Portal.CaptureReplayCursorAsync(reader, root.Quest.Id, default);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Result<GuildMessageReceipt> published = await f.Portal.PublishAsync(f.RunCaller(parentDriver), root.Quest.Id,
            Progress() with { Address = new(GuildCircleAddressKind.Child, second.Quest.Id, null, null) }, default);

        Assert.True(published.IsSuccess, published.Error.Message);

        Result<GuildPortalReplayPage> replay = await f.Portal.ReadReplayAsync(reader, root.Quest.Id, captured.Value.Cursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Empty(replay.Value.Page.Items);

        Assert.True(replay.Value.CaughtUp);
    }

    [Fact]
    public async Task Operator_inbox_reads_its_addressed_messages_without_a_domain_application_or_acknowledgement()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        Result<GuildMessageReceipt> published = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        Result<GuildCircleInboxPage> first = await f.Portal.ReadOperatorInboxAsync(f.Caller, new(root.Quest.Id, null), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        GuildCircleInboxItem item = Assert.Single(first.Value.Items);

        Assert.Equal(published.Value.MessageId, item.EnvelopeId);

        Assert.Equal(GuildDeliveryState.Pending, item.DeliveryState);

        Assert.NotNull(item.Envelope);

        Result<GuildCircleInboxPage> second = await f.Portal.ReadOperatorInboxAsync(f.Caller, new(root.Quest.Id, null), default);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(item.InboxId, Assert.Single(second.Value.Items).InboxId);

        Assert.Equal(0L, await f.CountAsync("guild_portal_receipts"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_inputs"));

        Assert.Equal(ErrorCodes.Guild.Forbidden, (await f.Portal.ReadOperatorInboxAsync(f.RunCaller(driver), new(root.Quest.Id, null), default)).Error.Code);
    }

    [Fact]
    public async Task An_addressed_parent_message_exposes_only_a_content_free_invalidation_to_its_child()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parentDriver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, parentDriver);

        GuildQuestDriver childDriver = await f.DriverAsync(child);

        GuildCallerContext reader = f.RunCaller(childDriver);

        Result<GuildPortalReplayCursor> captured = await f.Portal.CaptureReplayCursorAsync(reader, root.Quest.Id, default);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Result<GuildMessageReceipt> published = await f.Portal.PublishAsync(f.RunCaller(parentDriver), root.Quest.Id,
            Progress() with { Address = new(GuildCircleAddressKind.Child, child.Quest.Id, null, null) }, default);

        Assert.True(published.IsSuccess, published.Error.Message);

        Result<GuildPortalReplayPage> replay = await f.Portal.ReadReplayAsync(reader, root.Quest.Id, captured.Value.Cursor, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        GuildChangeEventDto invalidation = Assert.Single(replay.Value.Page.Items);

        Assert.Equal(published.Value.MessageId, invalidation.ObjectId);

        Assert.Empty(invalidation.ContentRefs);
    }

    [Fact]
    public async Task A_deleted_owner_requires_resync_while_content_free_event_history_survives()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Result<GuildPortalReplayCursor> captured = await f.Portal.CaptureReplayCursorAsync(f.Caller, root.Quest.Id, default);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        long events = await f.CountAsync("guild_events");

        await using (SqliteConnection connection = await f.OpenAsync())
        {
            using CovenantSqliteAuthorizationScope deletion = CovenantSqliteConnectionInitializer.Instance.Authorize(connection,
                CovenantSqliteAuthorizationKind.SessionRetention);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "DELETE FROM Sessions WHERE Id=$id";

            command.Parameters.AddWithValue("$id", root.Quest.SessionId!.Value.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(ErrorCodes.Guild.CursorGone, (await f.Portal.ReadReplayAsync(f.Caller, root.Quest.Id, captured.Value.Cursor, default)).Error.Code);

        Assert.Equal(events, await f.CountAsync("guild_events"));
    }

    [Fact]
    public async Task Operator_inbox_cursors_cannot_be_used_on_the_consumer_inbox_route()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await AppendReplayEventsAsync(f, root.Quest.Id, root.Quest.Id, 3);

        Result<GuildCircleInboxPage> first = await f.Portal.ReadOperatorInboxAsync(f.Caller, new(root.Quest.Id, null, 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Empty(first.Value.Items);

        Assert.NotNull(first.Value.NextCursor);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await f.Portal.ReadInboxAsync(f.Caller, new(root.Quest.Id, first.Value.NextCursor), default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await f.Portal.ReadOperatorInboxAsync(f.Caller, new(root.Quest.Id, first.Value.NextCursor + "x"), default)).Error.Code);
    }

    [Fact]
    public async Task Operator_metadata_hides_a_currently_refused_protected_reference_without_acknowledging_it()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        f.Host.PublicationLabel = new(Guid.NewGuid(), SensitiveArtifactKind.GuildContent, Guid.NewGuid(), root.Quest.SessionId, null, null,
            1, new(SHA256.HashData("source"u8)), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, DateTimeOffset.UtcNow);

        Result<GuildMessageReceipt> published = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        f.Host.DenyProtectedContent = true;

        Result<GuildCircleInboxPage> page = await f.Portal.ReadOperatorInboxAsync(f.Caller, new(root.Quest.Id, null), default);

        Assert.True(page.IsSuccess, page.Error.Message);

        GuildCircleInboxItem item = Assert.Single(page.Value.Items);

        Assert.Null(item.Envelope);

        Assert.Equal(GuildDeliveryState.Pending, item.DeliveryState);

        Assert.Equal(ErrorCodes.Guild.Forbidden, item.ReasonCode);

        Assert.Equal(published.Value.MessageId, item.EnvelopeId);

        Assert.Equal(0L, await f.CountAsync("guild_portal_receipts"));
    }

    [Fact]
    public async Task A_sibling_subtree_filter_is_not_a_quest_visibility_grant()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver parent = await f.DriverAsync(root);

        GuildQuestStored first = await f.ChildAsync(root, parent);

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        GuildQuestStored sibling = await f.ChildAsync(root, parent);

        GuildQuestDriver child = await f.DriverAsync(first);

        Result<GuildEventPage> page = await f.Portal.ReadEventsAsync(f.RunCaller(child),
            new(root.Quest.Id, sibling.Quest.Id, null, null), default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, page.Error.Code);
    }

    [Fact]
    public void Withheld_inbox_metadata_roundtrips_without_a_protected_envelope_reference()
    {
        const string json = "{\"inboxId\":\"11111111-1111-1111-1111-111111111111\",\"envelope\":null,\"revision\":2,\"deliveryState\":\"withheld\",\"reasonCode\":\"OwnerUnavailable\",\"envelopeId\":\"22222222-2222-2222-2222-222222222222\"}";

        GuildCircleInboxItem item = JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildCircleInboxItem)!;

        string encoded = JsonSerializer.Serialize(item, GuildJsonContext.Default.GuildCircleInboxItem);

        Assert.Contains("\"deliveryState\":\"withheld\"", encoded, StringComparison.Ordinal);

        Assert.Contains("\"reasonCode\":\"OwnerUnavailable\"", encoded, StringComparison.Ordinal);

        Assert.DoesNotContain("contentRef", encoded, StringComparison.Ordinal);
    }

    private static Task<Result<long>> AppendReplayEventsAsync(Fixture f, Guid root, Guid quest, int count) =>
        f.Authority.InQuestTransactionAsync<long>(root, async (connection, transaction, _, ct) =>
        {
            long last = 0;

            for (int i = 0; i < count; i++)
            {
                Result<long> appended = await GuildPortalEvents.AppendWithinAsync(connection, transaction, root, quest,
                    GuildEventKind.QuestChanged, Guid.NewGuid(), 1, null, DateTimeOffset.UtcNow, ct);

                if (appended.IsFailure) return appended.Error;

                last = appended.Value;
            }

            return last;
        }, default);
}
