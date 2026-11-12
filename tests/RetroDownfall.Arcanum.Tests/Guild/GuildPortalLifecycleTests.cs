using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildPortalLifecycleTests
{
    [Fact]
    public async Task Revocation_permanently_withholds_only_recipient_subtree_and_keeps_operator_diagnostics()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("""
            INSERT INTO guild_inbox(Id,EnvelopeId,RecipientKey,StreamId,SenderSequence,DeliveryStateCode,Revision,CreatedAtUtc)
            VALUES('18181818-1818-1818-1818-181818181818','12121212-1212-1212-1212-121212121212','operator',
                '16161616-1616-1616-1616-161616161616',1,1,1,'2000-01-01T00:00:00.0000000Z');
            """);

        await using SqliteTransaction transaction = f.Connection.BeginTransaction(deferred: false);

        Result revoked = await GuildPortalCleanupParticipant.WithholdRevokedWithinAsync(f.Connection, transaction,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), DateTimeOffset.UtcNow, default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);

        await transaction.CommitAsync();

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_inbox WHERE RecipientQuestId IS NOT NULL AND DeliveryStateCode=3 AND ReasonCode='Guild.AuthorityRevoked' AND LeaseOwner IS NULL"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_inbox WHERE RecipientKey='operator' AND DeliveryStateCode=1"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_portal_receipts WHERE EffectKindCode=3"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_content"));
    }

    [Fact]
    public async Task Portal_cleanup_final_check_refuses_pending_source_evidence()
    {
        Guid owner = Guid.Parse("19191919-1919-1919-1919-191919191919");

        await using Fixture f = await Fixture.CreateAsync(owner);

        await f.ExecuteAsync("""
            INSERT INTO guild_portal_outbox(SourceKindCode,ReceiptId,QuestId,CreatedAtUtc)
            VALUES(0,'15151515-1515-1515-1515-151515151515','11111111-1111-1111-1111-111111111111','2000-01-01T00:00:00.0000000Z');
            """);

        await using SqliteTransaction transaction = f.Connection.BeginTransaction(deferred: false);

        Result deleted = await new GuildPortalCleanupParticipant(TimeProvider.System).DeleteAsync(
            new(2, owner, null), f.Connection, transaction, default);

        Assert.True(deleted.IsFailure);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, deleted.Error.Code);
    }

    [Fact]
    public async Task Protected_erasure_withholds_every_pending_recipient_across_multiple_batches()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("""
            WITH RECURSIVE recipients(Number) AS (SELECT 1 UNION ALL SELECT Number+1 FROM recipients WHERE Number<205)
            INSERT INTO guild_inbox(Id,EnvelopeId,RecipientKey,RecipientQuestId,StreamId,SenderSequence,DeliveryStateCode,Revision,CreatedAtUtc)
            SELECT printf('%08X-1234-1234-1234-123456789ABC',Number),'12121212-1212-1212-1212-121212121212',
                'recipient-'||Number,'11111111-1111-1111-1111-111111111111','16161616-1616-1616-1616-161616161616',1,1,1,'2000-01-01T00:00:00.0000000Z'
            FROM recipients;
            """);

        var erased = await f.PurgeAsync();

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(206L, await f.CountAsync("SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=3"));

        Assert.Equal(206L, await f.CountAsync("SELECT COUNT(*) FROM guild_portal_receipts WHERE EffectKindCode=3"));

        Assert.Equal(0L, await f.CountAsync("SELECT COUNT(*) FROM guild_portal_inputs"));
    }

    [Fact]
    public async Task Protected_Portal_purge_withholds_pending_delivery_and_releases_claim_atomically()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var erased = await f.PurgeAsync();

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(0L, await f.CountAsync("SELECT COUNT(*) FROM guild_content"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=3 AND LeaseOwner IS NULL AND LeaseRevision IS NULL AND OperationId IS NULL AND ReasonCode='Guild.ContentErased' AND AcknowledgedAtUtc IS NOT NULL"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_portal_receipts WHERE EffectKindCode=3"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_envelopes WHERE SenderSequence=1"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE ContentKindCode=3 AND Revision=1 AND ByteLength=4"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_events WHERE EventKindCode=2 AND ObjectId='12121212-1212-1212-1212-121212121212' AND EnvelopeId='12121212-1212-1212-1212-121212121212'"));
    }

    [Fact]
    public async Task Failed_Portal_erasure_event_rolls_back_body_receipt_and_inbox_disposition()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("CREATE TRIGGER refuse_erasure_event BEFORE INSERT ON guild_events BEGIN SELECT RAISE(ABORT,'fixture event failure'); END");

        var erased = await f.PurgeAsync();

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.True(erased.Value.IsBlocked);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_content"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17"));

        Assert.Equal(0L, await f.CountAsync("SELECT COUNT(*) FROM guild_content_erasure_receipts"));

        Assert.Equal(0L, await f.CountAsync("SELECT COUNT(*) FROM guild_portal_receipts"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=1 AND LeaseOwner='old-holder' AND LeaseRevision=1"));
    }

    [Fact]
    public async Task Protected_erasure_preserves_previously_applied_delivery_and_its_exact_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("""
            INSERT INTO guild_portal_receipts VALUES('14141414-1414-1414-1414-141414141414','17171717-1717-1717-1717-171717171717',zeroblob(32),0,'2000-01-01T00:00:00.0000000Z');
            UPDATE guild_inbox SET DeliveryStateCode=2,Revision=Revision+1,LeaseOwner=NULL,LeaseRevision=NULL,OperationId=NULL,AcknowledgedAtUtc='2000-01-01T00:00:00.0000000Z';
            """);

        var erased = await f.PurgeAsync();

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=2 AND Revision=2"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_portal_receipts WHERE ApplicationReceiptId='17171717-1717-1717-1717-171717171717' AND EffectKindCode=0"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_events WHERE EventKindCode=2"));
    }

    [Fact]
    public async Task Conflicting_pending_application_receipt_blocks_erasure_without_overwriting_evidence()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("""
            INSERT INTO guild_portal_receipts VALUES('14141414-1414-1414-1414-141414141414','17171717-1717-1717-1717-171717171717',zeroblob(32),0,'2000-01-01T00:00:00.0000000Z');
            """);

        var erased = await f.PurgeAsync();

        Assert.True(erased.IsSuccess, erased.Error.Message);

        Assert.True(erased.Value.IsBlocked);

        Assert.Equal(0UL, erased.Value.ErasedCount);

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_content"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=1"));

        Assert.Equal(1L, await f.CountAsync("SELECT COUNT(*) FROM guild_portal_receipts WHERE EffectKindCode=0"));
    }

    private sealed class Fixture(SqliteConnection connection, ArtifactSensitivityLabel label) : IAsyncDisposable
    {
        internal SqliteConnection Connection => connection;

        internal static async Task<Fixture> CreateAsync(Guid? sessionId = null)
        {
            SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

            await GuildQuestSchemaTests.SeedQuestAsync(connection, sessionId);

            Guid contentId = Guid.Parse("13131313-1313-1313-1313-131313131313");

            byte[] body = [1, 2, 3, 4];

            CovenantDigest digest = new(SHA256.HashData(body));

            GenerationProvenance provenance = GenerationProvenance.Create([CovenantOperationGateFixture.DatasetGeneration]);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,ExactGenerationIds,CreatedAtUtc)
                VALUES('13131313-1313-1313-1313-131313131313',3,'12121212-1212-1212-1212-121212121212',1,$digest,'text/plain',$body,1,1,$generations,'2000-01-01T00:00:00.0000000Z');
                INSERT INTO guild_portal_streams VALUES('16161616-1616-1616-1616-161616161616','11111111-1111-1111-1111-111111111111',0,'producer',1);
                INSERT INTO guild_envelopes VALUES('12121212-1212-1212-1212-121212121212','16161616-1616-1616-1616-161616161616','request',zeroblob(32),'11111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111111',NULL,NULL,1,'13131313-1313-1313-1313-131313131313',1,'{}','2000-01-01T00:00:00.0000000Z');
                INSERT INTO guild_inbox VALUES('14141414-1414-1414-1414-141414141414','12121212-1212-1212-1212-121212121212','recipient','11111111-1111-1111-1111-111111111111','16161616-1616-1616-1616-161616161616',1,1,1,'old-holder',1,'22222222222222222222222222222222',NULL,'2000-01-01T00:00:00.0000000Z',NULL);
                """;

            command.Parameters.AddWithValue("$digest", digest.Bytes);

            command.Parameters.AddWithValue("$body", body);

            command.Parameters.AddWithValue("$generations", provenance.ToCanonicalExactBytes());

            _ = await command.ExecuteNonQueryAsync();

            Result<LabeledArtifactWriteReceipt> written = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction,
                new(SensitiveArtifactKind.GuildContent, contentId, null, null, null, 1, digest, ContentSensitivity.CovenantDerived, provenance), default);

            Assert.True(written.IsSuccess, written.Error.Message);

            await transaction.CommitAsync();

            var labelled = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, null, SensitiveArtifactKind.GuildContent, contentId, default);

            Assert.True(labelled.IsSuccess, labelled.Error.Message);

            Assert.NotNull(labelled.Value);

            return new(connection, labelled.Value);
        }

        internal async Task<Result<CovenantArtifactErasureProgress>> PurgeAsync()
        {
            CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

            await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize), default)).Value;

            CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority.ForExclusive(
                lease, CovenantExclusiveOperation.CovenantFamilyReinitialize).Value;

            CovenantProtectedArtifactErasureKernel kernel = new(new FixedCovenantConnectionSource(connection),
                CovenantSqliteConnectionInitializer.Instance, TimeProvider.System);

            CovenantProtectedArtifactErasureItem item = new(label.ArtifactId, SensitiveArtifactKind.GuildContent, null,
                label.LabelId, label, label.ArtifactContentDigest, 1);

            return await kernel.ErasePageAsync(new(CovenantOperationGateFixture.DatasetGeneration, [item]), authority, default);
        }

        internal async Task<long> CountAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            return (long)(await command.ExecuteScalarAsync())!;
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Applied_Portal_input_remains_pinned_while_its_recipient_execution_is_unresolved()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var published = await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "queued input"), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        var claimed = await f.Portal.ClaimAsync(driver, default);

        Assert.True(claimed.IsSuccess, claimed.Error.Message);

        var applied = await f.Portal.ApplyAsync(claimed.Value, default);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(1L, await f.CountAsync("guild_portal_inputs"));

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        await using SqliteCommand content = connection.CreateCommand();

        content.CommandText = "SELECT ContentId FROM guild_envelopes WHERE Id=$message";

        content.Parameters.AddWithValue("$message", published.Value.MessageId.ToString("D").ToUpperInvariant());

        string contentId = (string)(await content.ExecuteScalarAsync())!;

        ArcanumSettings settings = new();

        settings.Retention.GuildContent.Enabled = true;

        settings.Retention.GuildContent.Days = 1;

        FakeTimeProvider clock = new();

        clock.Advance(TimeSpan.FromDays(30));

        string paths = Path.Combine(Path.GetTempPath(), "arcanum-portal-retention-" + Guid.NewGuid().ToString("N"));

        DataRetentionService retention = new(db, new TestOptionsMonitor<ArcanumSettings>(settings),
            new LongRunningOperationStore(db, f.Connections), clock, NullLogger<DataRetentionService>.Instance,
            FixtureLabeledArtifactGuard.For(db), Path.Combine(paths, "attachments"), Path.Combine(paths, "files"), Path.Combine(paths, "logs"));

        DataRetentionPlan plan = await retention.PlanAsync(new(DataRetentionOperation.Prune));

        Assert.DoesNotContain("guild-content:" + contentId, plan.CandidateIds);
    }

    [Fact]
    public async Task Terminal_recipient_withholds_accepted_input_and_claim_without_suppressing_its_result_intentions()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var published = await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "accepted input"), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        var claimed = await f.Portal.ClaimAsync(driver, default);

        Assert.True(claimed.IsSuccess, claimed.Error.Message);

        GuildQuestResult result = PortalResult(root.Quest.Id, "completed result");

        GuildMutationIdentity mutation = new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest);

        var committed = await f.Store.CommitResultAsync(driver, mutation, root.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=3 AND Revision=3 AND LeaseOwner IS NULL AND LeaseRevision IS NULL AND OperationId IS NULL AND ReasonCode='Guild.Rejected'"));

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_portal_receipts WHERE EffectKindCode=3"));

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_events WHERE EventKindCode=1 AND ObjectId IN(SELECT Id FROM guild_inbox) AND Revision=3"));

        Assert.Equal(2L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_portal_outbox WHERE SourceKindCode IN(1,2) AND DeliveryStateCode=1"));

        Assert.True((await f.Portal.ApplyAsync(claimed.Value, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_portal_inputs"));

        var replay = await f.Store.CommitResultAsync(driver, mutation, root.Quest.Revision, result, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(committed.Value, replay.Value);

        Assert.Equal(1L, await f.CountAsync("guild_portal_receipts"));
    }

    [Theory]
    [InlineData("CREATE TRIGGER refuse_terminal_withholding BEFORE INSERT ON guild_portal_receipts BEGIN SELECT RAISE(ABORT,'fixture withholding failure'); END")]
    [InlineData("CREATE TRIGGER refuse_terminal_projection BEFORE INSERT ON guild_events WHEN NEW.EventKindCode=1 AND NEW.Revision>1 BEGIN SELECT RAISE(ABORT,'fixture projection failure'); END")]
    public async Task Failed_terminal_recipient_disposition_rolls_back_result_ledger_and_allocation_settlement(string failure)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var published = await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, "accepted input"), default);

        Assert.True(published.IsSuccess, published.Error.Message);

        await f.ExecuteAsync(failure);

        GuildQuestResult result = PortalResult(root.Quest.Id, "completed result");

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest),
            root.Quest.Revision, result, default);

        Assert.True(committed.IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(0L, await f.CountAsync("guild_result_outbox"));

        Assert.Equal(0L, await f.CountAsync("fixture_settlements"));

        Assert.Equal(0L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM LongRunningOperations WHERE State IN(4,5,6)"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_receipts"));

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=1 AND Revision=1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Erased_result_notice_withholds_pending_domain_delivery_without_reconstructing_source_bytes(bool hasBody)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var parentDriver = await f.DriverAsync(root);

        var child = await f.ChildAsync(root, parentDriver);

        var childDriver = await f.DriverAsync(child);

        GuildQuestResult result = PortalResult(child.Quest.Id, "source result");

        if (!hasBody)
        {
            result = result with
            {
                Body = null,
                Receipt = result.Receipt with
                {
                    Content = null,
                    CanonicalContentDigest = Convert.ToHexString(SHA256.HashData([])),
                    Outcome = QuestState.Rejected,
                    ReasonCode = ErrorCodes.Guild.Rejected,
                },
            };
        }

        var committed = await f.Store.CommitResultAsync(childDriver, new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest),
            child.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        child = (await f.Store.GetAsync(f.Caller, child.Quest.Id, default)).Value;

        var payload = await f.Authority.InQuestTransactionAsync<(string MimeType, string Body)>(child.Quest.RootQuestId,
            (c, t, _, ct) => GuildPortalStore.ReadHostNoticeWithinAsync(c, t, child, GuildEnvelopeKind.Result, ct), default);

        Assert.True(payload.IsSuccess, payload.Error.Message);

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, child.Quest.Id, Guid.NewGuid(), f.Host.InstallationGeneration,
            receiptId: result.Receipt.ResultId, receiptSource: "result");

        GuildPublishRequest request = new(result.Receipt.ResultId, new(GuildCircleAddressKind.Parent, root.Quest.Id, null, null),
            GuildEnvelopeKind.Result, payload.Value.MimeType, payload.Value.Body, null);

        var accepted = await f.Portal.PublishAsync(caller, child.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        GuildMessageReceipt notice = accepted.Value;

        Guid content;

        await using (SqliteConnection connection = await f.OpenAsync())
        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = "SELECT ContentId FROM guild_envelopes WHERE Id=$envelope";

            command.Parameters.AddWithValue("$envelope", notice.MessageId.ToString("D").ToUpperInvariant());

            content = Guid.Parse((string)(await command.ExecuteScalarAsync())!);

            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, content.ToString("N"), default);

            command.CommandText = "DELETE FROM guild_content WHERE ContentId=$content";

            command.Parameters.AddWithValue("$content", content.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_result_outbox WHERE DeliveryStateCode=3"));

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=3 AND ReasonCode='Guild.ContentErased'"));

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_portal_outbox WHERE SourceKindCode=1 AND DeliveryStateCode=3"));

        var replay = await f.Portal.PublishAsync(caller, child.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(notice.MessageId, replay.Value.MessageId);

        Assert.Equal(0L, await PortalLifecycleScalarAsync(f,
            "SELECT COUNT(*) FROM guild_content WHERE ContentId='" + content.ToString("D").ToUpperInvariant() + "'"));

        Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(1L, await f.CountAsync("fixture_settlements"));

        Assert.Equal(0L, await f.CountAsync("guild_dispatches"));
    }

    [Fact]
    public async Task Signet_revocation_withholds_addressed_inbox_and_commits_one_replayable_event()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var publication = await f.Portal.PublishAsync(f.Caller, root.Quest.Id,
            Progress() with { Kind = GuildEnvelopeKind.Intervention, Address = new(GuildCircleAddressKind.Child, root.Quest.Id, null, null) }, default);

        Assert.True(publication.IsSuccess, publication.Error.Message);

        Guid key = Guid.NewGuid();

        GuildMutationIdentity mutation = new(key, new string('A', 64));

        GuildRevokeSignetRequest request = new(key, root.Quest.SignetRevision, GuildCancellationReason.OperatorRequested);

        var revoked = await f.Authority.RevokeAsync(f.Caller, mutation, root.Quest.SignetId, request, default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);

        Assert.True(revoked.Value.EventWatermark > 0);

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=3 AND ReasonCode='Guild.AuthorityRevoked'"));

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_portal_receipts WHERE EffectKindCode=3"));

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_events WHERE EventKindCode=3"));

        var replayed = await f.Authority.RevokeAsync(f.Caller, mutation, root.Quest.SignetId, request, default);

        Assert.True(replayed.IsSuccess, replayed.Error.Message);

        Assert.NotNull(revoked.Value.Receipt);

        Assert.NotNull(replayed.Value.Receipt);

        Assert.False(revoked.Value.Receipt.Replayed);

        Assert.True(replayed.Value.Receipt.Replayed);

        Assert.Equal(revoked.Value with { Receipt = revoked.Value.Receipt with { Replayed = true } }, replayed.Value);

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_events WHERE EventKindCode=3"));
    }

    [Theory]
    [InlineData("CREATE TRIGGER fail_revocation_event BEFORE INSERT ON guild_events WHEN NEW.EventKindCode=3 BEGIN SELECT RAISE(ABORT,'fixture event failure'); END")]
    [InlineData("CREATE TRIGGER fail_revocation_receipt BEFORE INSERT ON guild_portal_receipts BEGIN SELECT RAISE(ABORT,'fixture receipt failure'); END")]
    public async Task Signet_revocation_rolls_back_authority_inbox_and_receipt_when_Portal_evidence_fails(string failure)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var publication = await f.Portal.PublishAsync(f.Caller, root.Quest.Id,
            Progress() with { Kind = GuildEnvelopeKind.Intervention, Address = new(GuildCircleAddressKind.Child, root.Quest.Id, null, null) }, default);

        Assert.True(publication.IsSuccess, publication.Error.Message);

        await f.ExecuteAsync(failure);

        Guid key = Guid.NewGuid();

        var revoked = await f.Authority.RevokeAsync(f.Caller, new(key, new string('A', 64)), root.Quest.SignetId,
            new(key, root.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

        Assert.True(revoked.IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_signet_revocations"));

        Assert.Equal(0L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_signets WHERE Revoked=1"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_receipts"));

        Assert.Equal(1L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=1"));

        Assert.Equal(0L, await PortalLifecycleScalarAsync(f, "SELECT COUNT(*) FROM guild_events WHERE EventKindCode=3"));
    }

    private static async Task<long> PortalLifecycleScalarAsync(Fixture fixture, string sql)
    {
        await using SqliteConnection connection = await fixture.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
