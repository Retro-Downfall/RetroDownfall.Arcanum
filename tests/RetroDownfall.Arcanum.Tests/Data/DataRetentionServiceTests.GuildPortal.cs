using System.Security.Cryptography;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    private const string PortalEnvelope = "12121212-1212-1212-1212-121212121212";

    private const string PortalBody = "13131313-1313-1313-1313-131313131313";

    private const string PortalInbox = "14141414-1414-1414-1414-141414141414";

    [SkippableFact]
    public async Task Portal_owner_cleanup_withholds_delivery_and_source_intentions_before_shared_cursor_advances()
    {
        RequireSqlCipher();

        (Guid session, _) = await SeedSessionAsync(pinned: false);

        await SeedRetentionPortalAsync(session);

        await ExecuteAsync("""
            INSERT INTO guild_portal_outbox(SourceKindCode,ReceiptId,QuestId,CreatedAtUtc)
            VALUES(0,'15151515-1515-1515-1515-151515151515','11111111-1111-1111-1111-111111111111','2000-01-01T00:00:00.0000000Z');
            """);

        await DeleteGuildTestOwnerAsync();

        var cleaned = await GuildCleanup([new GuildPortalCleanupParticipant(TimeProvider.System),
            new GuildQuestCleanupParticipant(FixtureOrdinaryConnectionFactory.For(_db!), TimeProvider.System)])
            .RunBatchAsync(8, 8, default);

        Assert.True(cleaned.IsSuccess, cleaned.Error.Message);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_inbox WHERE DeliveryStateCode=3 AND ReasonCode='Guild.OwnerUnavailable'"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_portal_outbox WHERE DeliveryStateCode=3 AND EnvelopeId IS NULL"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_portal_receipts WHERE EffectKindCode=3"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content WHERE ContentKindCode=3"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE ContentId='" + PortalBody + "'"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_envelopes WHERE SenderSequence=1"));

        Assert.True(await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode=2") > 0);
    }

    [SkippableFact]
    public async Task Portal_acknowledged_body_prune_retains_envelope_sequence_and_exact_erasure_evidence()
    {
        RequireSqlCipher();

        await SeedRetentionPortalAsync();

        await AcknowledgeRetentionPortalAsync();

        var service = CreateService(PortalPruneSettings());

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Contains("guild-content:" + PortalBody, plan.CandidateIds);

        var applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content WHERE ContentKindCode=3"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE ContentId='" + PortalBody + "' AND ContentKindCode=3 AND ByteLength=0"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_envelopes WHERE Id='" + PortalEnvelope + "' AND SenderSequence=1"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT COUNT(*) FROM guild_events WHERE EventKindCode=2 AND ObjectId='" + PortalEnvelope + "'"));
    }

    [SkippableFact]
    public async Task Portal_pending_inbox_pins_body_and_otherwise_settled_Quest_reset()
    {
        RequireSqlCipher();

        await SeedRetentionPortalAsync();

        DataRetentionPlan prune = await CreateService(PortalPruneSettings()).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.DoesNotContain("guild-content:" + PortalBody, prune.CandidateIds);

        DataRetentionPlan reset = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(reset.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("INSERT INTO guild_portal_receipts VALUES('14141414-1414-1414-1414-141414141414','17171717-1717-1717-1717-171717171717',randomblob(32),0,'2000-01-01T00:00:00.0000000Z');")]
    [InlineData("INSERT INTO guild_portal_receipts VALUES('14141414-1414-1414-1414-141414141414','17171717-1717-1717-1717-171717171717',zeroblob(32),3,'2000-01-01T00:00:00.0000000Z');")]
    public async Task Portal_terminal_inbox_without_matching_effect_evidence_pins_body_and_reset(string receipt)
    {
        RequireSqlCipher();

        await SeedRetentionPortalAsync();

        await ExecuteAsync(receipt + "UPDATE guild_inbox SET DeliveryStateCode=2,Revision=Revision+1,AcknowledgedAtUtc='2000-01-01T00:00:00.0000000Z'");

        DataRetentionPlan prune = await CreateService(PortalPruneSettings()).PlanAsync(new(DataRetentionOperation.Prune));

        Assert.DoesNotContain("guild-content:" + PortalBody, prune.CandidateIds);

        DataRetentionPlan reset = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(reset.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired);
    }

    [SkippableFact]
    public async Task Portal_unmaterialized_source_intent_pins_otherwise_settled_Quest_reset()
    {
        RequireSqlCipher();

        await SeedRetentionPortalAsync();

        await AcknowledgeRetentionPortalAsync();

        await ExecuteAsync("""
            INSERT INTO guild_portal_outbox(SourceKindCode,ReceiptId,QuestId,CreatedAtUtc)
            VALUES(0,'15151515-1515-1515-1515-151515151515','11111111-1111-1111-1111-111111111111','2000-01-01T00:00:00.0000000Z');
            """);

        DataRetentionPlan reset = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(reset.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired);
    }

    [SkippableFact]
    public async Task Portal_factory_reset_deletes_populated_evidence_in_child_before_parent_order()
    {
        RequireSqlCipher();

        await SeedRetentionPortalAsync();

        await AcknowledgeRetentionPortalAsync();

        await ExecuteAsync("""
            INSERT INTO guild_portal_inputs VALUES('14141414-1414-1414-1414-141414141414','11111111-1111-1111-1111-111111111111','12121212-1212-1212-1212-121212121212','2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_event_streams VALUES('11111111-1111-1111-1111-111111111111',1,0);
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,EnvelopeId,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111',1,1,'12121212-1212-1212-1212-121212121212',1,'11111111-1111-1111-1111-111111111111','12121212-1212-1212-1212-121212121212','2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_portal_outbox(SourceKindCode,ReceiptId,QuestId,EnvelopeId,CreatedAtUtc,DeliveryStateCode)
            VALUES(0,'15151515-1515-1515-1515-151515151515','11111111-1111-1111-1111-111111111111','12121212-1212-1212-1212-121212121212','2000-01-01T00:00:00.0000000Z',2);
            """);

        var recovered = await ReconcileFactoryResetV0Async(CreateService(), "portal-factory-reset-recovery");

        Assert.Equal(1, recovered.Summary.Completed);

        Assert.Equal(0, recovered.Summary.RequiresAttention);

        foreach (string table in new[] { "guild_portal_inputs", "guild_portal_receipts", "guild_inbox", "guild_envelopes", "guild_portal_streams", "guild_portal_outbox", "guild_events", "guild_event_streams" })
        {
            Assert.Equal(0, await CampaignScalarCountAsync("SELECT COUNT(*) FROM " + table));
        }
    }

    private static ArcanumSettings PortalPruneSettings()
    {
        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.GuildContent.Enabled = true;

        settings.Retention.GuildContent.Days = 1;

        return settings;
    }

    private async Task SeedRetentionPortalAsync(Guid? sessionId = null)
    {
        await SeedRetentionQuestAsync(sessionId);

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),4,1,1,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_content(ContentId,SessionId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES('13131313-1313-1313-1313-131313131313',@session,3,'12121212-1212-1212-1212-121212121212',1,@digest,'text/plain',X'',0,0,'2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_portal_streams VALUES('16161616-1616-1616-1616-161616161616','11111111-1111-1111-1111-111111111111',0,'producer',1);
            INSERT INTO guild_envelopes VALUES('12121212-1212-1212-1212-121212121212','16161616-1616-1616-1616-161616161616','request',zeroblob(32),'11111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111111',@session,NULL,1,'13131313-1313-1313-1313-131313131313',1,'{}','2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_inbox VALUES('14141414-1414-1414-1414-141414141414','12121212-1212-1212-1212-121212121212','recipient','11111111-1111-1111-1111-111111111111','16161616-1616-1616-1616-161616161616',1,1,1,NULL,NULL,NULL,NULL,'2000-01-01T00:00:00.0000000Z',NULL);
            """, ("@digest", SHA256.HashData([])), ("@session", sessionId is { } id ? id.ToString("D").ToUpperInvariant() : DBNull.Value));
    }

    private Task AcknowledgeRetentionPortalAsync() => ExecuteAsync("""
        INSERT INTO guild_portal_receipts VALUES('14141414-1414-1414-1414-141414141414','17171717-1717-1717-1717-171717171717',zeroblob(32),0,'2000-01-01T00:00:00.0000000Z');
        UPDATE guild_inbox SET DeliveryStateCode=2,Revision=Revision+1,AcknowledgedAtUtc='2000-01-01T00:00:00.0000000Z';
        """);
}
