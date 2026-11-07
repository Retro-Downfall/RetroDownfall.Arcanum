using System.Text.Json;
using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Run_publication_atomically_commits_one_recipient_and_replays_identical_intent()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(root.IsSuccess, root.Error.Message);

        var driver = await f.DriverAsync(root.Value);

        GuildCallerContext caller = f.RunCaller(driver);

        GuildPublishRequest request = Progress();

        var accepted = await f.Portal.PublishAsync(caller, root.Value.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        var replay = await f.Portal.PublishAsync(caller, root.Value.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(accepted.Value.MessageId, replay.Value.MessageId);

        Assert.True(replay.Value.Receipt!.Replayed);

        Assert.Equal(1L, await f.CountAsync("guild_envelopes"));

        Assert.Equal(1L, await f.CountAsync("guild_inbox"));

        var conflict = await f.Portal.PublishAsync(caller, root.Value.Quest.Id, request with { Body = "changed" }, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, conflict.Error.Code);

        await using var connection = await f.OpenAsync();

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT MetadataJson FROM guild_envelopes";

        GuildEnvelope envelope = JsonSerializer.Deserialize((string)(await command.ExecuteScalarAsync())!, GuildJsonContext.Default.GuildEnvelope)!;

        Assert.Equal(driver.Execution.AgentId, envelope.Producer.AgentId);

        Assert.Equal(driver.Execution.RunId, envelope.Producer.RunId);

        Assert.Equal(1, envelope.SenderSequence);

        Assert.Null(envelope.Producer.OperatorPrincipalId);
    }

    [Fact]
    public async Task Same_root_sibling_address_is_not_parent_or_own_child_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var rootDriver = await f.DriverAsync(root);

        var first = await f.ChildAsync(root, rootDriver);

        root = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        var second = await f.ChildAsync(root, rootDriver);

        var firstDriver = await f.DriverAsync(first);

        var result = await f.Portal.PublishAsync(f.RunCaller(firstDriver), first.Quest.Id,
            Progress() with { Address = new(GuildCircleAddressKind.Child, second.Quest.Id, null, null) }, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Theory]
    [InlineData(GuildEnvelopeKind.Result)]
    [InlineData(GuildEnvelopeKind.QuestIssued)]
    [InlineData(GuildEnvelopeKind.Settle)]
    [InlineData(GuildEnvelopeKind.Intervention)]
    public async Task Run_cannot_claim_host_or_operator_kinds(GuildEnvelopeKind kind)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var result = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress() with { Kind = kind }, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Publication_refuses_oversized_utf8_without_allocating_stream()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var result = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id,
            Progress() with { Body = new string('é', 32769) }, default);

        Assert.Equal(ErrorCodes.Guild.PayloadTooLarge, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_portal_streams"));
    }

    [Fact]
    public async Task Failed_inbox_insert_rolls_back_stream_content_envelope_and_projection()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        long content = await f.CountAsync("guild_content");

        await f.ExecuteAsync("CREATE TRIGGER fixture_refuse_inbox BEFORE INSERT ON guild_inbox BEGIN SELECT RAISE(ABORT,'fixture refusal'); END;");

        var result = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.True(result.IsFailure);

        Assert.Equal(content, await f.CountAsync("guild_content"));

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_streams"));
    }

    [Fact]
    public async Task Operator_intervention_retains_principal_identity_without_a_fabricated_run()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var request = Progress() with { Kind = GuildEnvelopeKind.Intervention, Address = new(GuildCircleAddressKind.Child, root.Quest.Id, null, null) };

        var result = await f.Portal.PublishAsync(f.Caller, root.Quest.Id, request, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        await using var connection = await f.OpenAsync();

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT MetadataJson FROM guild_envelopes";

        GuildEnvelope envelope = JsonSerializer.Deserialize((string)(await command.ExecuteScalarAsync())!, GuildJsonContext.Default.GuildEnvelope)!;

        Assert.Equal(GuildProducerKind.Operator, envelope.Producer.Kind);

        Assert.Equal(f.Caller.PrincipalId!.Value.ToString("D").ToUpperInvariant(), envelope.Producer.OperatorPrincipalId);

        Assert.Null(envelope.Producer.AgentId);

        Assert.Null(envelope.Producer.RunId);

        Assert.Null(envelope.Producer.QuestId);
    }

    [Fact]
    public async Task Host_receipt_cannot_publish_a_receipt_that_does_not_exist()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildCallerContext receipt = new(GuildCallerKind.HostReceipt, root.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: Guid.NewGuid(), receiptSource: "result");

        var result = await f.Portal.PublishAsync(receipt, root.Quest.Id, Progress() with { Kind = GuildEnvelopeKind.Result, IdempotencyKey = receipt.ReceiptId!.Value }, default);

        Assert.True(result.IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Concurrent_duplicate_requests_commit_one_original_acceptance()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        GuildCallerContext caller = f.RunCaller(driver);

        GuildPublishRequest request = Progress();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Portal.PublishAsync(caller, root.Quest.Id, request, default)));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error.Message));

        Assert.Single(results.Select(result => result.Value.MessageId).Distinct());

        Assert.Equal(1L, await f.CountAsync("guild_envelopes"));

        Assert.Equal(1L, await f.CountAsync("guild_inbox"));
    }

    [Fact]
    public async Task Exhausted_sender_stream_refuses_without_new_content_or_envelope()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        string producer = $"{driver.Execution.AgentId:D}:{driver.Execution.RunId:D}".ToUpperInvariant();

        await f.ExecuteAsync($"INSERT INTO guild_portal_streams VALUES('{Guid.NewGuid():D}','{root.Quest.Id:D}',0,'{producer}',9223372036854775807)".ToUpperInvariant());

        long bodies = await f.CountAsync("guild_content");

        var result = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.Equal(ErrorCodes.Guild.SequenceExhausted, result.Error.Code);

        Assert.Equal(bodies, await f.CountAsync("guild_content"));

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Run_derived_provenance_is_preserved_and_current_disclosure_can_refuse_body()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        f.Host.PublicationLabel = new(Guid.NewGuid(), SensitiveArtifactKind.GuildContent, Guid.NewGuid(), root.Quest.SessionId, null, null,
            1, new(SHA256.HashData("source"u8)), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, DateTimeOffset.UtcNow);

        var published = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        f.Host.DenyProtectedContent = true;

        var read = await f.Authority.InQuestTransactionAsync<byte[]>(root.Quest.Id, async (connection, transaction, signets, ct) =>
        {
            var envelope = await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, published.Value.MessageId, ct);

            Assert.True(envelope.IsSuccess, envelope.Error.Message);

            return await f.Portal.ReadBodyWithinAsync(connection, transaction, signets, f.Caller, envelope.Value, ct);
        }, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, read.Error.Code);

        f.Host.DenyProtectedContent = false;

        read = await f.Authority.InQuestTransactionAsync<byte[]>(root.Quest.Id, async (connection, transaction, signets, ct) =>
        {
            var envelope = await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, published.Value.MessageId, ct);

            return await f.Portal.ReadBodyWithinAsync(connection, transaction, signets, f.Caller, envelope.Value, ct);
        }, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal("Progress data", System.Text.Encoding.UTF8.GetString(read.Value));
    }

    [Fact]
    public async Task Malformed_stored_metadata_is_a_typed_refusal_before_materialization()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var published = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        await f.ExecuteAsync("DROP TRIGGER guild_envelopes_guard_update; UPDATE guild_envelopes SET MetadataJson='{}'");

        var read = await f.Authority.InQuestTransactionAsync<GuildEnvelope>(root.Quest.Id,
            (connection, transaction, _, ct) => GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, published.Value.MessageId, ct), default);

        Assert.True(read.IsFailure);
    }

    [Fact]
    public async Task Closed_recipient_refuses_new_intervention_but_preserves_accepted_retry()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var request = Progress() with { Kind = GuildEnvelopeKind.Intervention, Address = new(GuildCircleAddressKind.Child, root.Quest.Id, null, null) };

        var accepted = await f.Portal.PublishAsync(f.Caller, root.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        await f.ExecuteAsync($"UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1 WHERE Id='{root.Quest.Id:D}' COLLATE NOCASE");

        var replay = await f.Portal.PublishAsync(f.Caller, root.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(accepted.Value.MessageId, replay.Value.MessageId);

        var refused = await f.Portal.PublishAsync(f.Caller, root.Quest.Id, request with { IdempotencyKey = Guid.NewGuid() }, default);

        Assert.Equal(ErrorCodes.Guild.Rejected, refused.Error.Code);

        Assert.Equal(1L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Host_receipt_to_closed_parent_refuses_new_acceptance()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var child = await f.ChildAsync(root, driver);

        await f.ExecuteAsync($"UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1 WHERE Id='{root.Quest.Id:D}' COLLATE NOCASE");

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, child.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: child.Quest.Id, receiptSource: "questIssue");

        GuildPublishRequest request = new(child.Quest.Id, new(GuildCircleAddressKind.Parent, root.Quest.Id, null, null),
            GuildEnvelopeKind.QuestIssued, "text/plain", "Compute the child contribution", null);

        var result = await f.Portal.PublishAsync(caller, child.Quest.Id, request, default);

        Assert.Equal(ErrorCodes.Guild.Rejected, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Bodyless_result_publishes_only_the_exact_typed_receipt_notice()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        string emptyDigest = Convert.ToHexString(SHA256.HashData([]));

        GuildResultReceipt receipt = new(root.Quest.Id, Guid.NewGuid(), emptyDigest, null, QuestState.Rejected, ErrorCodes.Guild.Rejected);

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), emptyDigest), root.Quest.Revision,
            new(receipt, null, NoDispatchProven: true), default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, root.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: receipt.ResultId, receiptSource: "result");

        string notice = JsonSerializer.Serialize(receipt, GuildJsonContext.Default.GuildResultReceipt);

        GuildPublishRequest request = new(receipt.ResultId, new(GuildCircleAddressKind.Operator, null, null, null), GuildEnvelopeKind.Result,
            "application/json", notice, null);

        var forged = await f.Portal.PublishAsync(caller, root.Quest.Id, request with { Body = "{}" }, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, forged.Error.Code);

        var accepted = await f.Portal.PublishAsync(caller, root.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        var read = await f.Authority.InQuestTransactionAsync<byte[]>(root.Quest.Id, async (connection, transaction, signets, ct) =>
        {
            var envelope = await GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, accepted.Value.MessageId, ct);

            Assert.Equal(GuildContentKind.CircleBody, envelope.Value.ContentRef.Kind);

            Assert.Equal(GuildProducerKind.HostReceipt, envelope.Value.Producer.Kind);

            Assert.Null(envelope.Value.Producer.RunId);

            return await f.Portal.ReadBodyWithinAsync(connection, transaction, signets, f.Caller, envelope.Value, ct);
        }, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(notice, System.Text.Encoding.UTF8.GetString(read.Value));

        Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));
    }

    [Fact]
    public async Task Host_domain_receipt_cannot_redirect_from_operator_to_own_child()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var child = await f.ChildAsync(root, driver);

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, root.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: root.Quest.Id, receiptSource: "questIssue");

        GuildPublishRequest request = new(root.Quest.Id, new(GuildCircleAddressKind.Child, child.Quest.Id, null, null),
            GuildEnvelopeKind.QuestIssued, "text/plain", "Summarize the selected material", null);

        var result = await f.Portal.PublishAsync(caller, root.Quest.Id, request, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Restored_source_receipt_cannot_republish_to_operator_without_recipient_signet()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await f.ExecuteAsync($"UPDATE guild_signets SET RestoredInert=1,Revision=Revision+1 WHERE SignetId='{root.Quest.SignetId:D}' COLLATE NOCASE");

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, root.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: root.Quest.Id, receiptSource: "questIssue");

        GuildPublishRequest request = new(root.Quest.Id, new(GuildCircleAddressKind.Operator, null, null, null),
            GuildEnvelopeKind.QuestIssued, "text/plain", "Summarize the selected material", null);

        var result = await f.Portal.PublishAsync(caller, root.Quest.Id, request, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Publication_stamps_current_authority_revision_separately_from_credential_generation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        await f.ExecuteAsync($"UPDATE guild_signets SET Revision=Revision+1 WHERE SignetId='{root.Quest.SignetId:D}' COLLATE NOCASE");

        var current = (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value;

        Assert.NotEqual(driver.Execution.CredentialGeneration, current.Quest.SignetRevision);

        var published = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        var envelope = await f.Authority.InQuestTransactionAsync<GuildEnvelope>(root.Quest.Id,
            (connection, transaction, _, ct) => GuildPortalStore.ReadEnvelopeWithinAsync(connection, transaction, published.Value.MessageId, ct), default);

        Assert.True(envelope.IsSuccess, envelope.Error.Message);

        Assert.Equal(current.Quest.SignetRevision, envelope.Value.AuthorityGeneration);
    }

    [Fact]
    public async Task Retired_authority_epoch_cannot_republish_source_receipt_to_operator()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await f.ExecuteAsync($"UPDATE guild_signet_authority_state SET Epoch='{Guid.NewGuid():D}'".ToUpperInvariant());

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, root.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: root.Quest.Id, receiptSource: "questIssue");

        GuildPublishRequest request = new(root.Quest.Id, new(GuildCircleAddressKind.Operator, null, null, null),
            GuildEnvelopeKind.QuestIssued, "text/plain", "Summarize the selected material", null);

        var result = await f.Portal.PublishAsync(caller, root.Quest.Id, request, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Terminal_run_replays_accepted_publication_but_cannot_append_new_progress()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        GuildCallerContext caller = f.RunCaller(driver);

        GuildPublishRequest request = Progress();

        var accepted = await f.Portal.PublishAsync(caller, root.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        string digest = Convert.ToHexString(SHA256.HashData([]));

        GuildResultReceipt receipt = new(root.Quest.Id, Guid.NewGuid(), digest, null, QuestState.Rejected, ErrorCodes.Guild.Rejected);

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), digest), root.Quest.Revision,
            new(receipt, null, NoDispatchProven: true), default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        var replay = await f.Portal.PublishAsync(caller, root.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(accepted.Value.MessageId, replay.Value.MessageId);

        var refused = await f.Portal.PublishAsync(caller, root.Quest.Id, request with { IdempotencyKey = Guid.NewGuid() }, default);

        Assert.Equal(ErrorCodes.Guild.Rejected, refused.Error.Code);
    }

    [Fact]
    public async Task Mismatched_goal_label_cannot_be_inherited_as_valid_publication_provenance()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var injected = await f.Authority.InQuestTransactionAsync<LabeledArtifactWriteReceipt>(root.Quest.Id,
            (connection, transaction, _, ct) => ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction,
                new(SensitiveArtifactKind.GuildContent, root.Quest.GoalRef.ContentId, root.Quest.SessionId, null, null,
                    99, new(SHA256.HashData("wrong bytes"u8)), ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.NewGuid()])), ct), default);

        Assert.True(injected.IsSuccess, injected.Error.Message);

        var result = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, Progress(), default);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Host_receipt_revision_precondition_does_not_conflict_with_outbox_replay()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, root.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: root.Quest.Id, receiptSource: "questIssue");

        GuildPublishRequest request = new(root.Quest.Id, new(GuildCircleAddressKind.Operator, null, null, null),
            GuildEnvelopeKind.QuestIssued, "text/plain", "Summarize the selected material", root.Quest.Revision);

        var accepted = await f.Portal.PublishAsync(caller, root.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        var drained = await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Assert.Equal(1, drained.Value);

        Assert.Equal(1L, await f.CountAsync("guild_envelopes"));

        Assert.Equal(1L, await PortalScalarAsync(f, $"SELECT COUNT(*) FROM guild_portal_outbox WHERE DeliveryStateCode=2 AND EnvelopeId='{accepted.Value.MessageId:D}' COLLATE NOCASE"));

        var conflict = await f.Portal.PublishAsync(caller, root.Quest.Id, request with { Body = "changed", ExpectedRevision = null }, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, conflict.Error.Code);
    }

    [Fact]
    public async Task Revision_is_checked_for_new_publication_and_does_not_change_prior_logical_intent()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        GuildCallerContext caller = f.RunCaller(driver);

        GuildPublishRequest request = Progress() with { ExpectedRevision = root.Quest.Revision };

        var accepted = await f.Portal.PublishAsync(caller, root.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        var replay = await f.Portal.PublishAsync(caller, root.Quest.Id, request with { ExpectedRevision = long.MaxValue }, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(accepted.Value.MessageId, replay.Value.MessageId);

        var refused = await f.Portal.PublishAsync(caller, root.Quest.Id,
            request with { ExpectedRevision = long.MaxValue, IdempotencyKey = Guid.NewGuid() }, default);

        Assert.Equal(ErrorCodes.Guild.RevisionConflict, refused.Error.Code);
    }

    private static GuildPublishRequest Progress() => new(Guid.NewGuid(), new(GuildCircleAddressKind.Operator, null, null, null), GuildEnvelopeKind.Progress, "text/plain", "Progress data", null);
}
