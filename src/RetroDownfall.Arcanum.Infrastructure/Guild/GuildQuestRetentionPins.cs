namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Evidence predicates only; execution remains owned by the existing operation ledger.</summary>
internal static class GuildQuestRetentionPins
{
    // Every caller binds its Quest as c. Missing proof is an obligation, never measured zero.
    internal const string ExecutionUnresolved = """
        (c.AccountingStateCode<>0
         OR NOT EXISTS(SELECT 1 FROM LongRunningOperations operation WHERE operation.Id=c.OperationId AND operation.State IN(4,5,6))
         OR NOT EXISTS(SELECT 1 FROM guild_result_receipts terminal WHERE terminal.QuestId=c.Id
             AND (terminal.NoDispatchProven=1 OR terminal.WorkStoppedProven=1))
         OR EXISTS(SELECT 1 FROM guild_dispatches dispatch WHERE dispatch.QuestId=c.Id
             AND (dispatch.EvidenceCode=2 OR (dispatch.EvidenceCode=3 AND NOT EXISTS(
                 SELECT 1 FROM guild_result_receipts stopped WHERE stopped.QuestId=c.Id AND stopped.WorkStoppedProven=1))))
         OR EXISTS(SELECT 1 FROM guild_result_outbox delivery WHERE delivery.QuestId=c.Id AND delivery.DeliveryStateCode=1))
        """;

    internal const string UnresolvedPortalInbox = """
        (i.DeliveryStateCode=1 OR i.AcknowledgedAtUtc IS NULL OR NOT EXISTS(
            SELECT 1 FROM guild_portal_receipts receipt WHERE receipt.InboxId=i.Id AND receipt.Digest=e.IntentDigest
              AND ((i.DeliveryStateCode=2 AND receipt.EffectKindCode IN(0,1,2))
                OR (i.DeliveryStateCode=3 AND receipt.EffectKindCode=3))))
        """;

    internal const string Unresolved = "(" + ExecutionUnresolved + """
         OR EXISTS(SELECT 1 FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId
             WHERE (i.RecipientQuestId=c.Id OR e.QuestId=c.Id) AND
        """ + UnresolvedPortalInbox + """
        ) OR EXISTS(SELECT 1 FROM guild_portal_outbox delivery WHERE delivery.QuestId=c.Id AND delivery.DeliveryStateCode=1))
        """;

    internal const string ContentReferences = """
        EXISTS(SELECT 1 FROM guild_quests retained WHERE retained.GoalContentId=guild_content_erasure_receipts.ContentId)
        OR EXISTS(SELECT 1 FROM guild_result_receipts retained WHERE retained.ContentId=guild_content_erasure_receipts.ContentId)
        OR EXISTS(SELECT 1 FROM guild_continuations retained WHERE retained.ContentId=guild_content_erasure_receipts.ContentId)
        OR EXISTS(SELECT 1 FROM guild_envelopes retained WHERE retained.ContentId=guild_content_erasure_receipts.ContentId)
        """;
}
