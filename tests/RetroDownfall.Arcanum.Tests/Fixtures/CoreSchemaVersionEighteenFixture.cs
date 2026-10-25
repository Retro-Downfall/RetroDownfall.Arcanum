using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>Published v18, frozen before the first v19 head edit.</summary>
internal static class CoreSchemaVersionEighteenFixture
{
    internal const string PublishedFingerprint = "077C7BC1524C581489538ACEDCF7F3BED9C0C076F88B03A2718CF2BAF8EF1FD7";

    private static readonly HashSet<string> PublishedObjects =
    [
        "Apprentices",
        "BatchAccountingRecoveryClaims",
        "BatchLineCheckpoints",
        "Batches",
        "BillableOperations",
        "BudgetAlerts",
        "BudgetReservations",
        "Campaigns",
        "CostAdjustments",
        "Entries",
        "IdempotencyClaims",
        "IdempotencyKeys",
        "InferenceRuns",
        "LongRunningOperations",
        "MageSettings",
        "Prompts",
        "SanctumBreaches",
        "SessionAttachments",
        "SessionContextPins",
        "Sessions",
        "UnseenServantWatermarks",
        "UploadedFiles",
        "WorkspaceContexts",
        "annal_claims",
        "annal_dependencies",
        "annal_heads",
        "annal_review_decision_receipts",
        "annal_review_events",
        "annal_review_markers",
        "annal_versions",
        "artifact_sensitivity",
        "assistant_entry_erasure_receipts",
        "assistant_entry_finalizations",
        "assistant_finalization_capacity_reservations",
        "attachment_memory_consultations",
        "campaign_contribution_artifacts",
        "campaign_contribution_state",
        "campaign_fork_frontiers",
        "campaign_maintenance_checkpoints",
        "campaign_path_full_reset_cleanup_evidence",
        "campaign_path_identities",
        "campaign_path_marker_intents",
        "campaign_path_operation_receipts",
        "campaign_registry_state",
        "campaign_rollup_artifacts",
        "campaign_rollup_sources",
        "campaign_rollup_state",
        "capability_cleanup_state",
        "covenant_authority_state",
        "covenant_schema_repair_intents",
        "disclosure_subject_aggregates",
        "disclosure_subject_state",
        "entry_embeddings",
        "external_disclosure_receipts",
        "external_disclosure_state",
        "grimoire_feature_schemas",
        "grimoire_schema_transitions",
        "installation_turn_quota_state",
        "lexicon_annal_fact_provenance",
        "lexicon_entries",
        "lexicon_fact_attachment_provenance",
        "local_erasure_work_items",
        "long_rest_accepted_summaries",
        "long_rest_adjudications",
        "long_rest_discovery_edges",
        "long_rest_discovery_embeddings",
        "long_rest_discovery_generations",
        "long_rest_discovery_leaves",
        "long_rest_discovery_nodes",
        "long_rest_protected_receipt_evidence",
        "long_rest_protected_receipt_inputs",
        "long_rest_provider_effects",
        "long_rest_receipt_inputs",
        "long_rest_receipts",
        "long_rest_sources",
        "long_rest_suppressions",
        "long_rest_sweeps",
        "long_rest_work_inputs",
        "long_rest_work_items",
        "long_running_operation_request_identities",
        "managed_file_write_intents",
        "memory_erasure_fingerprints",
        "memory_erasure_receipt_subjects",
        "memory_erasure_receipts",
        "owner_deletion_events",
        "owner_deletion_operation_intents",
        "protected_session_transfer_blobs",
        "protected_session_transfer_intents",
        "restored_managed_file_authority_tombstones",
        "saga_extraction_watermarks",
        "saga_memories",
        "saga_memory_attachment_provenance",
        "saga_memory_embeddings",
        "saga_retirement_suppressions",
        "saga_suppression_key",
        "session_attachment_chunks",
        "session_attachment_embeddings",
        "session_attachment_index_state",
        "session_campaign_binding_resolution_receipts",
        "session_campaign_bindings",
        "session_sensitivity_state",
        "session_summary_artifacts",
        "session_summary_state",
        "session_title_artifacts",
        "session_title_state",
        "session_turn_claims",
        "session_turn_maintenance_steps",
        "session_turn_quota_state",
        "tapestry_generations",
        "tapestry_leaf_hashes",
        "tapestry_node_embeddings",
        "tapestry_nodes",
        "workspace_file_chunks",
        "workspace_file_embeddings",
        "Entries_fts",
        "lexicon_fts",
        "long_rest_clean_fts",
        "long_rest_protected_fts",
        "Campaigns_Id_guard_identity_insert",
        "Campaigns_Id_guard_identity_update",
        "Campaigns_owner_deletion_event",
        "Entries_Id_guard_identity_insert",
        "Entries_Id_guard_identity_update",
        "Entries_SessionId_guard_identity_insert",
        "Entries_SessionId_guard_identity_update",
        "Entries_ad",
        "Entries_ai",
        "Entries_au",
        "Entries_campaign_contribution_delete",
        "Entries_campaign_contribution_insert",
        "Entries_campaign_contribution_update",
        "Entries_drop_tapestry_leaf_hash_delete",
        "Entries_drop_tapestry_leaf_hash_insert",
        "Entries_drop_tapestry_leaf_hash_update",
        "SessionAttachments_EntryId_guard_identity_insert",
        "SessionAttachments_EntryId_guard_identity_update",
        "SessionAttachments_Id_guard_identity_insert",
        "SessionAttachments_Id_guard_identity_update",
        "SessionAttachments_SessionId_guard_identity_insert",
        "SessionAttachments_SessionId_guard_identity_update",
        "Sessions_CampaignId_guard_identity_insert",
        "Sessions_CampaignId_guard_identity_update",
        "Sessions_Id_guard_identity_insert",
        "Sessions_Id_guard_identity_update",
        "Sessions_owner_deletion_event",
        "Sessions_turn_quota_state",
        "TR_BatchLineCheckpoints_IncrementOutcome",
        "TR_BatchLineCheckpoints_IncrementTotal",
        "annal_claims_guard_update",
        "annal_dependencies_guard_update",
        "annal_heads_validate_update",
        "annal_review_events_head_insert",
        "annal_review_events_head_update",
        "annal_versions_guard_update",
        "artifact_sensitivity_SessionId_guard_identity_insert",
        "artifact_sensitivity_campaign_contribution_delete",
        "artifact_sensitivity_campaign_contribution_insert",
        "artifact_sensitivity_guard_delete",
        "artifact_sensitivity_guard_update",
        "assistant_entry_erasure_receipts_guard_delete",
        "assistant_entry_erasure_receipts_guard_update",
        "assistant_entry_finalizations_AssistantEntryId_guard_identity_insert",
        "assistant_entry_finalizations_SessionId_guard_identity_insert",
        "assistant_entry_finalizations_guard_delete",
        "assistant_entry_finalizations_guard_update",
        "assistant_entry_finalizations_validate_insert",
        "assistant_finalization_capacity_reservations_guard_delete",
        "assistant_finalization_capacity_reservations_validate_insert",
        "assistant_finalization_capacity_reservations_validate_update",
        "attachment_memory_consultations_AttachmentId_guard_identity_insert",
        "attachment_memory_consultations_AttachmentId_guard_identity_update",
        "campaign_contribution_artifacts_guard_binding",
        "campaign_contribution_artifacts_guard_delete",
        "campaign_contribution_artifacts_guard_update",
        "campaign_contribution_state_campaign_delete",
        "campaign_contribution_state_campaign_insert",
        "campaign_contribution_state_campaign_update",
        "campaign_contribution_state_guard_binding",
        "campaign_contribution_state_guard_delete",
        "campaign_contribution_state_guard_insert",
        "campaign_contribution_state_guard_update",
        "campaign_fork_frontiers_guard_delete",
        "campaign_fork_frontiers_guard_update",
        "campaign_maintenance_checkpoints_guard_delete",
        "campaign_maintenance_checkpoints_guard_insert",
        "campaign_maintenance_checkpoints_guard_update",
        "campaign_maintenance_checkpoints_output_insert",
        "campaign_maintenance_checkpoints_output_update",
        "campaign_path_full_reset_cleanup_evidence_guard_delete",
        "campaign_path_full_reset_cleanup_evidence_guard_insert",
        "campaign_path_full_reset_cleanup_evidence_guard_update",
        "campaign_path_marker_intents_guard_delete",
        "campaign_path_marker_intents_guard_insert",
        "campaign_path_marker_intents_guard_update",
        "campaign_path_operation_receipts_guard_delete",
        "campaign_path_operation_receipts_guard_update",
        "campaign_registry_state_campaign_delete",
        "campaign_registry_state_campaign_insert",
        "campaign_rollup_artifacts_guard_delete",
        "campaign_rollup_artifacts_guard_update",
        "campaign_rollup_sources_campaign_delete",
        "campaign_rollup_sources_guard_delete",
        "campaign_rollup_sources_guard_insert",
        "campaign_rollup_sources_guard_update",
        "campaign_rollup_state_guard_delete",
        "campaign_rollup_state_guard_update",
        "covenant_schema_repair_intents_guard_delete",
        "covenant_schema_repair_intents_guard_update",
        "disclosure_subject_aggregates_guard_delete",
        "disclosure_subject_state_guard_delete",
        "entry_embeddings_EntryId_guard_identity_insert",
        "entry_embeddings_EntryId_guard_identity_update",
        "external_disclosure_receipts_guard_delete",
        "external_disclosure_receipts_guard_update",
        "external_disclosure_state_guard_delete",
        "installation_turn_quota_state_validate_insert",
        "installation_turn_quota_state_validate_update",
        "lexicon_entries_ad",
        "lexicon_entries_ai",
        "lexicon_entries_au",
        "lexicon_fact_attachment_provenance_AttachmentId_guard_identity_insert",
        "lexicon_fact_attachment_provenance_AttachmentId_guard_identity_update",
        "local_erasure_work_items_guard_delete",
        "local_erasure_work_items_guard_insert",
        "local_erasure_work_items_guard_update",
        "long_rest_accepted_summaries_guard_update",
        "long_rest_adjudications_guard_update",
        "long_rest_attachment_delete",
        "long_rest_attachment_insert",
        "long_rest_attachment_update",
        "long_rest_dependency_delete",
        "long_rest_dependency_insert",
        "long_rest_discovery_leaves_guard_update",
        "long_rest_edges_validate_insert",
        "long_rest_embedding_delete",
        "long_rest_embedding_insert",
        "long_rest_embedding_update",
        "long_rest_generation_complete",
        "long_rest_generation_delete",
        "long_rest_generation_invalidate",
        "long_rest_head_delete",
        "long_rest_head_insert",
        "long_rest_head_update",
        "long_rest_label_delete",
        "long_rest_label_insert",
        "long_rest_node_delete",
        "long_rest_node_label_delete",
        "long_rest_operation_delete",
        "long_rest_protected_receipt_evidence_guard_update",
        "long_rest_protected_receipt_inputs_guard_update",
        "long_rest_receipt_delete",
        "long_rest_receipt_inputs_guard_update",
        "long_rest_receipts_guard_update",
        "long_rest_saga_delete",
        "long_rest_saga_insert",
        "long_rest_saga_update",
        "long_rest_sources_deleted",
        "long_rest_sources_mutated",
        "long_rest_suppressions_guard_update",
        "long_rest_suppressions_validate_insert",
        "long_rest_work_item_delete",
        "long_running_operation_request_identities_guard_update",
        "managed_file_write_intents_guard_delete",
        "managed_file_write_intents_guard_insert",
        "managed_file_write_intents_guard_update",
        "memory_erasure_receipts_guard_update",
        "owner_deletion_events_guard_delete",
        "owner_deletion_events_guard_update",
        "owner_deletion_operation_intents_guard_delete",
        "owner_deletion_operation_intents_guard_insert",
        "owner_deletion_operation_intents_guard_update",
        "protected_session_transfer_blobs_guard_delete",
        "protected_session_transfer_blobs_guard_update",
        "protected_session_transfer_intents_guard_delete",
        "protected_session_transfer_intents_guard_update",
        "restored_managed_file_authority_tombstones_guard_delete",
        "restored_managed_file_authority_tombstones_guard_insert",
        "restored_managed_file_authority_tombstones_guard_update",
        "saga_extraction_watermarks_validate_insert",
        "saga_extraction_watermarks_validate_update",
        "saga_memories_CampaignId_guard_identity_insert",
        "saga_memories_CampaignId_guard_identity_update",
        "saga_memory_attachment_provenance_AttachmentId_guard_identity_insert",
        "saga_memory_attachment_provenance_AttachmentId_guard_identity_update",
        "saga_retirement_suppressions_CampaignId_guard_identity_insert",
        "saga_retirement_suppressions_CampaignId_guard_identity_update",
        "session_attachment_chunks_AttachmentId_guard_identity_insert",
        "session_attachment_chunks_AttachmentId_guard_identity_update",
        "session_attachment_chunks_drop_tapestry_leaf_hash_delete",
        "session_attachment_chunks_drop_tapestry_leaf_hash_insert",
        "session_attachment_chunks_drop_tapestry_leaf_hash_update",
        "session_attachment_index_state_AttachmentId_guard_identity_insert",
        "session_attachment_index_state_AttachmentId_guard_identity_update",
        "session_campaign_binding_resolution_receipts_guard_delete",
        "session_campaign_binding_resolution_receipts_guard_update",
        "session_campaign_bindings_CampaignId_guard_identity_insert",
        "session_campaign_bindings_CampaignId_guard_identity_update",
        "session_campaign_bindings_SessionId_guard_identity_insert",
        "session_campaign_bindings_guard_delete",
        "session_campaign_bindings_guard_insert",
        "session_campaign_bindings_guard_update",
        "session_sensitivity_state_SessionId_guard_identity_insert",
        "session_sensitivity_state_SessionId_guard_identity_update",
        "session_summary_artifacts_guard_delete",
        "session_summary_artifacts_guard_update",
        "session_title_artifacts_guard_delete",
        "session_title_artifacts_guard_update",
        "session_turn_claims_guard_delete",
        "session_turn_claims_validate_insert",
        "session_turn_claims_validate_update",
        "session_turn_maintenance_steps_guard_delete",
        "session_turn_maintenance_steps_validate_update",
        "session_turn_quota_state_guard_delete",
        "session_turn_quota_state_validate_insert",
        "session_turn_quota_state_validate_update",
        "workspace_file_chunks_drop_tapestry_leaf_hash_delete",
        "workspace_file_chunks_drop_tapestry_leaf_hash_insert",
        "workspace_file_chunks_drop_tapestry_leaf_hash_update",
        "grimoire_utc_instant_columns",
    ];

    private const string FrozenUtcView =
        """
        CREATE VIEW IF NOT EXISTS grimoire_utc_instant_columns AS
        SELECT 'Apprentices' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'Apprentices' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'BatchLineCheckpoints' AS TableName, 'CompletedAt' AS ColumnName
        UNION ALL SELECT 'BatchLineCheckpoints' AS TableName, 'DispatchedAt' AS ColumnName
        UNION ALL SELECT 'Batches' AS TableName, 'CompletedAt' AS ColumnName
        UNION ALL SELECT 'Batches' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'BillableOperations' AS TableName, 'CompletedAt' AS ColumnName
        UNION ALL SELECT 'BillableOperations' AS TableName, 'StartedAt' AS ColumnName
        UNION ALL SELECT 'BudgetAlerts' AS TableName, 'AlertedAt' AS ColumnName
        UNION ALL SELECT 'BudgetReservations' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'BudgetReservations' AS TableName, 'ExpiresAt' AS ColumnName
        UNION ALL SELECT 'BudgetReservations' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'Campaigns' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'Campaigns' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'CostAdjustments' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'Entries' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'IdempotencyClaims' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'IdempotencyClaims' AS TableName, 'HeartbeatAt' AS ColumnName
        UNION ALL SELECT 'IdempotencyClaims' AS TableName, 'LeaseExpiresAt' AS ColumnName
        UNION ALL SELECT 'IdempotencyClaims' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'IdempotencyKeys' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'InferenceRuns' AS TableName, 'CompletedAt' AS ColumnName
        UNION ALL SELECT 'InferenceRuns' AS TableName, 'StartedAt' AS ColumnName
        UNION ALL SELECT 'LongRunningOperations' AS TableName, 'CompletedAt' AS ColumnName
        UNION ALL SELECT 'LongRunningOperations' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'LongRunningOperations' AS TableName, 'HeartbeatAt' AS ColumnName
        UNION ALL SELECT 'LongRunningOperations' AS TableName, 'LeaseExpiresAt' AS ColumnName
        UNION ALL SELECT 'LongRunningOperations' AS TableName, 'StartedAt' AS ColumnName
        UNION ALL SELECT 'MageSettings' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'Prompts' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'Prompts' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'SanctumBreaches' AS TableName, 'OccurredAt' AS ColumnName
        UNION ALL SELECT 'SessionAttachments' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'SessionAttachments' AS TableName, 'SourceLastWriteAt' AS ColumnName
        UNION ALL SELECT 'SessionContextPins' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'SessionContextPins' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'Sessions' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'Sessions' AS TableName, 'LastSummarizedMessageAt' AS ColumnName
        UNION ALL SELECT 'Sessions' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'UnseenServantWatermarks' AS TableName, 'LastRunAt' AS ColumnName
        UNION ALL SELECT 'UploadedFiles' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'WorkspaceContexts' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'annal_claims' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'annal_dependencies' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'annal_heads' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'annal_versions' AS TableName, 'RecordedAtUtc' AS ColumnName
        UNION ALL SELECT 'annal_versions' AS TableName, 'ValidFromUtc' AS ColumnName
        UNION ALL SELECT 'annal_versions' AS TableName, 'ValidToUtc' AS ColumnName
        UNION ALL SELECT 'artifact_sensitivity' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'assistant_entry_erasure_receipts' AS TableName, 'ErasedAtUtc' AS ColumnName
        UNION ALL SELECT 'assistant_entry_finalizations' AS TableName, 'FinalizedAtUtc' AS ColumnName
        UNION ALL SELECT 'assistant_finalization_capacity_reservations' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'assistant_finalization_capacity_reservations' AS TableName, 'StateChangedAtUtc' AS ColumnName
        UNION ALL SELECT 'attachment_memory_consultations' AS TableName, 'MaterializedAt' AS ColumnName
        UNION ALL SELECT 'campaign_contribution_artifacts' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_contribution_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_fork_frontiers' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_maintenance_checkpoints' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_path_identities' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_path_marker_intents' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_path_marker_intents' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_path_operation_receipts' AS TableName, 'CompletedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_rollup_artifacts' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_rollup_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'capability_cleanup_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'covenant_authority_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'covenant_schema_repair_intents' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'covenant_schema_repair_intents' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'disclosure_subject_aggregates' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'disclosure_subject_state' AS TableName, 'ClosedAtUtc' AS ColumnName
        UNION ALL SELECT 'disclosure_subject_state' AS TableName, 'LastHeartbeatAtUtc' AS ColumnName
        UNION ALL SELECT 'external_disclosure_receipts' AS TableName, 'DisclosedAtUtc' AS ColumnName
        UNION ALL SELECT 'external_disclosure_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'grimoire_feature_schemas' AS TableName, 'InstalledAtUtc' AS ColumnName
        UNION ALL SELECT 'grimoire_schema_transitions' AS TableName, 'StartedAtUtc' AS ColumnName
        UNION ALL SELECT 'grimoire_schema_transitions' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'lexicon_annal_fact_provenance' AS TableName, 'MaterializedAt' AS ColumnName
        UNION ALL SELECT 'lexicon_entries' AS TableName, 'PinnedAtUtc' AS ColumnName
        UNION ALL SELECT 'lexicon_entries' AS TableName, 'RetiredAtUtc' AS ColumnName
        UNION ALL SELECT 'lexicon_entries' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'lexicon_fact_attachment_provenance' AS TableName, 'MaterializedAt' AS ColumnName
        UNION ALL SELECT 'local_erasure_work_items' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'local_erasure_work_items' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'long_running_operation_request_identities' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'managed_file_write_intents' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'managed_file_write_intents' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'owner_deletion_events' AS TableName, 'DeletedAtUtc' AS ColumnName
        UNION ALL SELECT 'owner_deletion_operation_intents' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'owner_deletion_operation_intents' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'protected_session_transfer_blobs' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'protected_session_transfer_blobs' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'long_rest_receipts' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'protected_session_transfer_intents' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'protected_session_transfer_intents' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'restored_managed_file_authority_tombstones' AS TableName, 'RecordedAtUtc' AS ColumnName
        UNION ALL SELECT 'saga_extraction_watermarks' AS TableName, 'LastExtractedEntryCreatedAt' AS ColumnName
        UNION ALL SELECT 'saga_memories' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'saga_memory_attachment_provenance' AS TableName, 'MaterializedAt' AS ColumnName
        UNION ALL SELECT 'saga_retirement_suppressions' AS TableName, 'RetiredAtUtc' AS ColumnName
        UNION ALL SELECT 'saga_suppression_key' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'session_attachment_chunks' AS TableName, 'ExtractedAt' AS ColumnName
        UNION ALL SELECT 'session_attachment_chunks' AS TableName, 'IndexedAt' AS ColumnName
        UNION ALL SELECT 'session_attachment_index_state' AS TableName, 'ExtractedAt' AS ColumnName
        UNION ALL SELECT 'session_attachment_index_state' AS TableName, 'IndexedAt' AS ColumnName
        UNION ALL SELECT 'session_attachment_index_state' AS TableName, 'PendingExtractedAt' AS ColumnName
        UNION ALL SELECT 'session_attachment_index_state' AS TableName, 'UpdatedAt' AS ColumnName
        UNION ALL SELECT 'session_campaign_binding_resolution_receipts' AS TableName, 'ResolvedAtUtc' AS ColumnName
        UNION ALL SELECT 'session_campaign_bindings' AS TableName, 'BoundAtUtc' AS ColumnName
        UNION ALL SELECT 'session_sensitivity_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'session_summary_artifacts' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'session_summary_artifacts' AS TableName, 'SummarizedThroughUtc' AS ColumnName
        UNION ALL SELECT 'session_summary_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'session_title_artifacts' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'session_title_state' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'session_turn_claims' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'session_turn_claims' AS TableName, 'HeartbeatAtUtc' AS ColumnName
        UNION ALL SELECT 'session_turn_claims' AS TableName, 'LeaseDeadlineUtc' AS ColumnName
        UNION ALL SELECT 'session_turn_claims' AS TableName, 'PreRequestHistoryWatermarkUtc' AS ColumnName
        UNION ALL SELECT 'session_turn_claims' AS TableName, 'TerminalAtUtc' AS ColumnName
        UNION ALL SELECT 'session_turn_maintenance_steps' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'tapestry_generations' AS TableName, 'CompletedAt' AS ColumnName
        UNION ALL SELECT 'tapestry_generations' AS TableName, 'StartedAt' AS ColumnName
        UNION ALL SELECT 'tapestry_nodes' AS TableName, 'CreatedAt' AS ColumnName
        UNION ALL SELECT 'workspace_file_chunks' AS TableName, 'FileLastWriteTime' AS ColumnName
        UNION ALL SELECT 'workspace_file_chunks' AS TableName, 'IndexedAt' AS ColumnName
        UNION ALL SELECT 'long_rest_discovery_generations' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'long_rest_accepted_summaries' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'long_rest_adjudications' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'long_rest_sweeps' AS TableName, 'ParkedAt' AS ColumnName
        UNION ALL SELECT 'long_rest_sweeps' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'long_rest_provider_effects' AS TableName, 'UpdatedAtUtc' AS ColumnName;
        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects => CoreSchemaVersionTwentyOneFixture.Objects.Where(value => PublishedObjects.Contains(value.Name)).Select(value => value.Name == "grimoire_utc_instant_columns" ? value with { Sql = FrozenUtcView }
        : value.Name == "external_disclosure_receipts" ? value with { Sql = PublishedDisclosureReceiptSchema.Sql } : value).ToArray();

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() => new(
    [
        new GrimoireSchemaVersionChain(GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Core, GrimoireSchemaTransactionTier.Core, 18, Fingerprint, Objects), Objects, [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core).Steps.Where(step => step.ToVersion <= 18)]),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
        GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
    ]);
}
