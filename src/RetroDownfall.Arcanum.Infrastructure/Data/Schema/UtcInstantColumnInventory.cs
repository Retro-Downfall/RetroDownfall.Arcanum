namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Closed inventory of every TEXT column whose value is an instant rather than a calendar label,
/// duration, counter, or integer tick watermark.
/// </summary>
/// <remarks>
/// <c>BudgetReservations.BudgetPeriod</c> is deliberately absent: it is a UTC calendar-day key, not
/// an instant. The two disclosure aggregate tick watermarks are deliberately absent too: they remain
/// INTEGER so numeric comparisons retain their meaning.
/// </remarks>
internal static class UtcInstantColumnInventory
{
    internal static IReadOnlyList<UtcInstantTable> Core { get; } =
    [
        new("Apprentices", ["CreatedAt", "UpdatedAt"]),
        new("BatchLineCheckpoints", ["CompletedAt", "DispatchedAt"]),
        new("Batches", ["CompletedAt", "CreatedAt"]),
        new("BillableOperations", ["CompletedAt", "StartedAt"]),
        new("BudgetAlerts", ["AlertedAt"]),
        new("BudgetReservations", ["CreatedAt", "ExpiresAt", "UpdatedAt"]),
        new("Campaigns", ["CreatedAt", "UpdatedAt"]),
        new("CostAdjustments", ["CreatedAt"]),
        new("Entries", ["CreatedAt"]),
        new("IdempotencyClaims", ["CreatedAt", "HeartbeatAt", "LeaseExpiresAt", "UpdatedAt"]),
        new("IdempotencyKeys", ["CreatedAt"]),
        new("InferenceRuns", ["CompletedAt", "StartedAt"]),
        new("LongRunningOperations", ["CompletedAt", "CreatedAt", "HeartbeatAt", "LeaseExpiresAt", "StartedAt"]),
        new("MageSettings", ["UpdatedAt"]),
        new("Prompts", ["CreatedAt", "UpdatedAt"]),
        new("SanctumBreaches", ["OccurredAt"]),
        new("SessionAttachments", ["CreatedAt", "SourceLastWriteAt"]),
        new("SessionContextPins", ["CreatedAt", "UpdatedAt"]),
        new("Sessions", ["CreatedAt", "LastSummarizedMessageAt", "UpdatedAt"]),
        new("UnseenServantWatermarks", ["LastRunAt"]),
        new("UploadedFiles", ["CreatedAt"]),
        new("WorkspaceContexts", ["CreatedAt"]),
        new("annal_claims", ["CreatedAtUtc"]),
        new("annal_dependencies", ["CreatedAtUtc"]),
        new("annal_heads", ["UpdatedAtUtc"]),
        new("annal_versions", ["RecordedAtUtc", "ValidFromUtc", "ValidToUtc"]),
        new("artifact_sensitivity", ["CreatedAtUtc"]),
        new("assistant_entry_erasure_receipts", ["ErasedAtUtc"]),
        new("assistant_entry_finalizations", ["FinalizedAtUtc"]),
        new("assistant_finalization_capacity_reservations", ["CreatedAtUtc", "StateChangedAtUtc"]),
        new("attachment_memory_consultations", ["MaterializedAt"]),
        new("campaign_path_identities", ["UpdatedAtUtc"]),
        new("campaign_path_marker_intents", ["CreatedAtUtc", "UpdatedAtUtc"]),
        new("campaign_path_operation_receipts", ["CompletedAtUtc"]),
        new("capability_cleanup_state", ["UpdatedAtUtc"]),
        new("covenant_authority_state", ["UpdatedAtUtc"]),
        new("covenant_schema_repair_intents", ["CreatedAtUtc", "UpdatedAtUtc"]),
        new("disclosure_subject_aggregates", ["UpdatedAtUtc"]),
        new("disclosure_subject_state", ["ClosedAtUtc", "LastHeartbeatAtUtc"]),
        new("external_disclosure_receipts", ["DisclosedAtUtc"]),
        new("external_disclosure_state", ["UpdatedAtUtc"]),
        new("grimoire_feature_schemas", ["InstalledAtUtc"]),
        new("grimoire_schema_transitions", ["StartedAtUtc", "UpdatedAtUtc"]),
        new("lexicon_entries", ["UpdatedAt"]),
        new("lexicon_fact_attachment_provenance", ["MaterializedAt"]),
        new("local_erasure_work_items", ["CreatedAtUtc", "UpdatedAtUtc"]),
        new("long_running_operation_request_identities", ["CreatedAtUtc"]),
        new("managed_file_write_intents", ["CreatedAtUtc", "UpdatedAtUtc"]),
        new("owner_deletion_events", ["DeletedAtUtc"]),
        new("owner_deletion_operation_intents", ["CreatedAtUtc", "UpdatedAtUtc"]),
        new("protected_session_transfer_blobs", ["CreatedAtUtc", "UpdatedAtUtc"]),
        new("protected_session_transfer_intents", ["CreatedAtUtc", "UpdatedAtUtc"]),
        new("restored_managed_file_authority_tombstones", ["RecordedAtUtc"]),
        new("saga_extraction_watermarks", ["LastExtractedEntryCreatedAt"]),
        new("saga_memories", ["CreatedAt"]),
        new("saga_memory_attachment_provenance", ["MaterializedAt"]),
        new("saga_retirement_suppressions", ["RetiredAtUtc"]),
        new("saga_suppression_key", ["CreatedAtUtc"]),
        new("session_attachment_chunks", ["ExtractedAt", "IndexedAt"]),
        new("session_attachment_index_state", ["ExtractedAt", "IndexedAt", "PendingExtractedAt", "UpdatedAt"]),
        new("session_campaign_binding_resolution_receipts", ["ResolvedAtUtc"]),
        new("session_campaign_bindings", ["BoundAtUtc"]),
        new("session_sensitivity_state", ["UpdatedAtUtc"]),
        new("session_summary_artifacts", ["CreatedAtUtc", "SummarizedThroughUtc"]),
        new("session_summary_state", ["UpdatedAtUtc"]),
        new("session_title_artifacts", ["CreatedAtUtc"]),
        new("session_title_state", ["UpdatedAtUtc"]),
        new("session_turn_claims", ["CreatedAtUtc", "HeartbeatAtUtc", "LeaseDeadlineUtc", "PreRequestHistoryWatermarkUtc", "TerminalAtUtc"]),
        new("session_turn_maintenance_steps", ["UpdatedAtUtc"]),
        new("tapestry_generations", ["CompletedAt", "StartedAt"]),
        new("tapestry_nodes", ["CreatedAt"]),
        new("workspace_file_chunks", ["FileLastWriteTime", "IndexedAt"]),
    ];

    internal static IReadOnlyList<UtcInstantTable> CovenantCanonical { get; } =
    [
        new("covenant_curation_heads", ["UpdatedAtUtc"]),
        new("covenant_curation_receipts", ["CommittedAtUtc"]),
        new("covenant_curation_versions", ["CreatedAtUtc"]),
        new("covenant_entries", ["CreatedAtUtc"]),
        new("covenant_heads", ["UpdatedAtUtc"]),
        new("covenant_key_epochs", ["UpdatedAtUtc"]),
        new("covenant_mutation_receipts", ["CommittedAtUtc"]),
        new("covenant_state", ["UpdatedAtUtc"]),
        new("covenant_turn_receipt_aggregate", ["EarliestCoveredAtUtc", "LatestCoveredAtUtc", "UpdatedAtUtc"]),
        new("covenant_turn_receipts", ["CreatedAtUtc"]),
        new("covenant_versions", ["CreatedAtUtc"]),
    ];
}

internal sealed record UtcInstantTable(string TableName, IReadOnlyList<string> Columns);
