using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The scope-wide ceilings a batch has to fit inside, and the one comparison that decides it.
/// </summary>
/// <remarks>
/// Pure, and in Core, because two callers must reach the same verdict from the same numbers. The
/// publication authority asks inside the transaction that will commit; the agent staging preflight
/// asks on a read lease, before a tool call tells the model its proposal was accepted. When those two
/// disagreed the disagreement was paid by the operator: staging admitted a proposal the commit then
/// refused, and the refusal arrived inside the transaction carrying the turn's reply, so the answer
/// was discarded along with the proposal. Copying the chain to the preflight would have recreated
/// that gap the first time either copy learned about a ceiling the other did not.
///
/// <para>Section ceilings are <see cref="CovenantSectionCapacity"/> and are deliberately separate:
/// these bound a scope's stored rows, those bound what one rendered Section may contain.</para>
/// </remarks>
public static class CovenantScopeCapacity
{

    /// <summary>
    /// The first ceiling this demand would breach, or <c>null</c> when every one of them holds.
    /// </summary>
    public static Error? Refusal(CovenantQuotaSnapshot snapshot, CovenantQuotaDemand demand)
    {

        ArgumentNullException.ThrowIfNull(snapshot);

        ArgumentNullException.ThrowIfNull(demand);

        return Exceeds(snapshot.ActiveEntriesInScope, demand.NewEntries, CovenantLimits.MaxStableEntriesPerScope, "stable entries in this scope")
            ?? Exceeds(snapshot.VersionsInScope, demand.NewVersions, CovenantLimits.MaxVersionsPerScope, "versions in this scope")
            ?? Exceeds(snapshot.SetVersionsInScope, demand.NewSetVersions, CovenantLimits.MaxSetVersionsPerScope, "content versions in this scope")
            ?? Exceeds(snapshot.CanonicalBytesInScope, demand.NewCanonicalBytes, CovenantLimits.MaxCanonicalBytesPerScope, "canonical bytes in this scope")
            ?? Exceeds(snapshot.AgentVersionsInCampaign, demand.NewAgentVersions, CovenantLimits.MaxAgentVersionsPerCampaign, "agent versions in this Campaign")
            ?? Exceeds(snapshot.AgentBytesInCampaign, demand.NewAgentBytes, CovenantLimits.MaxAgentBytesPerCampaign, "agent bytes in this Campaign")
            ?? Exceeds(snapshot.MutationReceiptsInScope, demand.NewMutationReceipts, CovenantLimits.MaxMutationReceiptsPerScope, "mutation receipts in this scope")
            ?? Exceeds(snapshot.ProvenanceRowsInCampaign, demand.NewProvenanceRows, CovenantLimits.MaxAttachmentProvenanceRowsPerCampaign, "attachment provenance rows in this Campaign")
            ?? Exceeds(snapshot.PendingOutboxRows, demand.NewOutboxRows, CovenantLimits.MaxPendingSearchOutboxRows, "pending search-outbox rows")
            // Every ceiling above bounds one scope, but a turn loads Global and one Campaign
            // together. Without this pair bound a batch that stays inside its own scope can seat a
            // combination no snapshot may carry, and the store then refuses the whole turn load with
            // an integrity failure that the operator cannot act on and never caused.
            ?? Exceeds(snapshot.ActiveEntriesInWidestTurnLoad, demand.NewEntries, CovenantLimits.MaxActiveSnapshotRows, "active heads one turn would load");

    }

    private static Error? Exceeds(long current, long added, long ceiling, string what) =>
        checked(current + added) > ceiling
            ? new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                $"This mutation would exceed the bound on {what}.")
            : null;

}
