namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Evidence predicates only; execution remains owned by the existing operation ledger.</summary>
internal static class GuildQuestRetentionPins
{
    // A stop receipt cannot erase an unresolved external operation or retained physical writer.
    // Unknown paid usage on a completed effect belongs to accounting, not execution liveness.
    internal const string PhysicalWorkUnresolved = """
        (EXISTS(SELECT 1 FROM guild_dispatches dispatch WHERE dispatch.QuestId=c.Id AND dispatch.EvidenceCode=2)
         OR EXISTS(SELECT 1 FROM guild_workspace_leases lease WHERE lease.QuestId=c.Id
             AND (lease.StateCode IS NOT 3 OR lease.InFlightMutationCount IS NOT 0)))
        """;

    internal const string ExternalWorkUnresolved = "(" + PhysicalWorkUnresolved + """
         OR EXISTS(SELECT 1 FROM guild_budget_effects effect WHERE effect.QuestId=c.Id AND effect.StateCode IN(0,1)))
        """;

    internal const string StoppedExecutionUnresolved = "(" + ExternalWorkUnresolved + """
         OR NOT EXISTS(SELECT 1 FROM LongRunningOperations operation WHERE operation.Id=c.OperationId AND operation.State IN(4,5,6))
         OR NOT EXISTS(SELECT 1 FROM guild_result_receipts terminal WHERE terminal.QuestId=c.Id
             AND (terminal.NoDispatchProven=1 OR terminal.WorkStoppedProven=1))
         OR EXISTS(SELECT 1 FROM guild_dispatches dispatch WHERE dispatch.QuestId=c.Id AND dispatch.EvidenceCode=3
             AND NOT EXISTS(SELECT 1 FROM guild_result_receipts stopped WHERE stopped.QuestId=c.Id AND stopped.WorkStoppedProven=1)))
        """;

    // Every caller binds its Quest as c. Missing proof is an obligation, never measured zero.
    internal const string ExecutionUnresolved = """
        (c.AccountingStateCode<>0
         OR EXISTS(SELECT 1 FROM guild_quest_budget binding JOIN guild_budget_accounts account ON account.AccountId=binding.AccountId
             WHERE binding.QuestId=c.Id AND account.Closed=0)
         OR EXISTS(SELECT 1 FROM guild_budget_effects effect WHERE effect.QuestId=c.Id
             AND (effect.StateCode IN(0,1) OR effect.UnknownUsd=1 OR effect.UnknownTokens=1
                 OR NOT EXISTS(SELECT 1 FROM guild_budget_evidence receipt
                     WHERE receipt.EffectId=effect.EffectId AND receipt.AppliedRevision=effect.Revision)))
         OR
        """ + StoppedExecutionUnresolved + """
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
        OR EXISTS(SELECT 1 FROM guild_local_executions retained WHERE retained.InputContentId=guild_content_erasure_receipts.ContentId)
        OR EXISTS(SELECT 1 FROM guild_remote_executions retained WHERE retained.InputContentId=guild_content_erasure_receipts.ContentId)
        OR EXISTS(SELECT 1 FROM guild_remote_questions retained WHERE retained.ContentId=guild_content_erasure_receipts.ContentId)
        OR EXISTS(SELECT 1 FROM guild_local_wait_checkpoints retained WHERE retained.ContentId=guild_content_erasure_receipts.ContentId)
        OR EXISTS(SELECT 1 FROM guild_envelopes retained WHERE retained.ContentId=guild_content_erasure_receipts.ContentId)
        """;
}
