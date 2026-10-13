using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The published Core v17 tree, captured before discovery head definitions changed.</summary>
internal static class CoreSchemaVersionSeventeenFixture
{
    internal const string PublishedFingerprint =
        "91088B7AAFED7C79D0D737836E96071D4F7586535BE49596226F33E9ACA08ACA";
    private static readonly HashSet<string> PublishedObjects =
    [
        "Entries_fts",
        "lexicon_fts",
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
        "long_rest_receipt_inputs",
        "long_rest_receipts",
        "long_rest_suppressions",
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
        "long_rest_receipt_inputs_guard_update",
        "long_rest_receipts_guard_update",
        "long_rest_suppressions_guard_update",
        "long_rest_suppressions_validate_insert",
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

    private static readonly IReadOnlyDictionary<string, string> Frozen = new Dictionary<string, string>
    {
        ["artifact_sensitivity_guard_delete"] =
            """
            -- Deleting a label is deleting the only record that an artifact is tainted, so it is allowed only
            -- from a scope that is also removing or replacing the artifact itself: artifact replacement,
            -- sensitivity retention purge, Session retention, owner cleanup, or Covenant family maintenance.
            -- Every one of these begins FALSE on each connection, so ordinary application work reaches this
            -- guard and aborts rather than quietly leaving a tainted artifact with no label.
            CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_guard_delete
            BEFORE DELETE ON artifact_sensitivity
            WHEN arcanum_artifact_replacement_authorized() = 0
                AND arcanum_sensitivity_purge_authorized() = 0
                AND arcanum_session_retention_authorized() = 0
                AND arcanum_owner_cleanup_authorized() = 0
                AND arcanum_covenant_family_maintenance_authorized() = 0
            BEGIN
                SELECT RAISE(ABORT, 'artifact_sensitivity delete requires an authorized replacement, purge, retention, or maintenance scope.');
            END;

            """,
        ["artifact_sensitivity"] =
            """
            -- The core, content-free information-flow ledger. A row records that one artifact is Covenant
            -- derived and where that taint came from, so reset, purge, and cache filters can find every tainted
            -- sink without reopening the artifact. Only a tainted artifact carries a label: an untainted one has
            -- no row at all, which is why exact provenance requires at least one generation rather than zero.
            -- Session, Campaign, and turn are historical owner identities without foreign keys, because a label
            -- outlives the turn that produced it and is retired through the owner-deletion journal.
            CREATE TABLE IF NOT EXISTS "artifact_sensitivity" (
                LabelId TEXT NOT NULL PRIMARY KEY,
                ArtifactKindCode INTEGER NOT NULL CHECK (ArtifactKindCode IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15)),
                ArtifactId TEXT NOT NULL,
                SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (1)),
                ProvenanceModeCode INTEGER NOT NULL CHECK (ProvenanceModeCode IN (1, 2)),
                ExactGenerationIds BLOB NULL,
                GenerationBloom BLOB NULL,
                SessionId TEXT NULL,
                CampaignId TEXT NULL,
                TurnId TEXT NULL,
                ArtifactRevision INTEGER NOT NULL CHECK (ArtifactRevision >= 0),
                ArtifactContentDigest BLOB NOT NULL CHECK (length(ArtifactContentDigest) = 32),
                SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
                ProducingPlanDigest BLOB NULL CHECK (ProducingPlanDigest IS NULL OR length(ProducingPlanDigest) = 32),
                ProducingAdmissionDigest BLOB NULL CHECK (ProducingAdmissionDigest IS NULL OR length(ProducingAdmissionDigest) = 32),
                ProducingMaintenanceReceiptDigest BLOB NULL CHECK (ProducingMaintenanceReceiptDigest IS NULL OR length(ProducingMaintenanceReceiptDigest) = 32),
                ArtifactLabelDigest BLOB NOT NULL CHECK (length(ArtifactLabelDigest) = 32),
                CreatedAtUtc TEXT NOT NULL,
                -- Exact provenance packs 1 to 8 raw 16-byte generation identities; BloomOverflow is the fixed
                -- 256-bit bitset and nothing else. A row carrying both, neither, or a truncated vector would
                -- claim a provenance it cannot reproduce, and the sensitivity digest computed from it would
                -- never verify. An all-zero Bloom is refused because an overflow always has bits set.
                CHECK (
                    (ProvenanceModeCode = 1
                        AND GenerationBloom IS NULL
                        AND ExactGenerationIds IS NOT NULL
                        AND length(ExactGenerationIds) BETWEEN 16 AND 128
                        AND length(ExactGenerationIds) % 16 = 0)
                    OR (ProvenanceModeCode = 2
                        AND ExactGenerationIds IS NULL
                        AND GenerationBloom IS NOT NULL
                        AND length(GenerationBloom) = 32
                        AND GenerationBloom <> zeroblob(32))
                ),
                -- The exact vector is canonically sorted, and for raw big-endian identities that is the memcmp
                -- order SQLite already compares blobs in. Strictly increasing also proves the slots hold
                -- distinct generations, so a duplicate cannot pad the vector past the eight-generation ceiling
                -- and suppress the overflow switch.
                CHECK (
                    ExactGenerationIds IS NULL
                    OR (
                        (length(ExactGenerationIds) < 32
                            OR substr(ExactGenerationIds, 1, 16) < substr(ExactGenerationIds, 17, 16))
                        AND (length(ExactGenerationIds) < 48
                            OR substr(ExactGenerationIds, 17, 16) < substr(ExactGenerationIds, 33, 16))
                        AND (length(ExactGenerationIds) < 64
                            OR substr(ExactGenerationIds, 33, 16) < substr(ExactGenerationIds, 49, 16))
                        AND (length(ExactGenerationIds) < 80
                            OR substr(ExactGenerationIds, 49, 16) < substr(ExactGenerationIds, 65, 16))
                        AND (length(ExactGenerationIds) < 96
                            OR substr(ExactGenerationIds, 65, 16) < substr(ExactGenerationIds, 81, 16))
                        AND (length(ExactGenerationIds) < 112
                            OR substr(ExactGenerationIds, 81, 16) < substr(ExactGenerationIds, 97, 16))
                        AND (length(ExactGenerationIds) < 128
                            OR substr(ExactGenerationIds, 97, 16) < substr(ExactGenerationIds, 113, 16))
                    )
                ),
                -- The label digest binds the plan and the admission it produced as a pair. One without the other
                -- would let a label claim a current Covenant admission it cannot show a plan for.
                CHECK (
                    (ProducingPlanDigest IS NULL AND ProducingAdmissionDigest IS NULL)
                    OR (ProducingPlanDigest IS NOT NULL AND ProducingAdmissionDigest IS NOT NULL)
                )
            );

            -- One live label per artifact. Two labels for the same artifact would let a purge remove one and
            -- leave the artifact still evidenced as tainted by the other.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_artifact_sensitivity_artifact
                ON artifact_sensitivity(ArtifactKindCode, ArtifactId);

            CREATE INDEX IF NOT EXISTS idx_artifact_sensitivity_session
                ON artifact_sensitivity(SessionId);

            CREATE INDEX IF NOT EXISTS idx_artifact_sensitivity_campaign
                ON artifact_sensitivity(CampaignId);

            CREATE INDEX IF NOT EXISTS idx_artifact_sensitivity_turn
                ON artifact_sensitivity(TurnId);
            """.ReplaceLineEndings("\n") + "\n",

        ["long_rest_receipts"] =
            """
            -- One immutable, content-free disposition of an explicitly supplied exact-version manifest.
            -- Applied and no-change decisions use the same receipt grammar; neither stores memory plaintext.
            CREATE TABLE IF NOT EXISTS long_rest_receipts (
                ReceiptId TEXT NOT NULL PRIMARY KEY,
                PolicyVersion INTEGER NOT NULL CHECK (PolicyVersion = 1),
                KindCode INTEGER NOT NULL CHECK (KindCode BETWEEN 1 AND 3),
                OutcomeCode INTEGER NOT NULL CHECK (OutcomeCode IN (1, 2)),
                ReasonCode INTEGER NOT NULL CHECK (ReasonCode BETWEEN 0 AND 10),
                InputHash TEXT NOT NULL CHECK (length(InputHash) = 64),
                OutputHash TEXT NOT NULL CHECK (length(OutputHash) = 64),
                RequestedSurvivorVersionId TEXT NULL REFERENCES annal_versions(VersionId),
                SurvivorMemoryId TEXT NULL,
                SurvivorVersionId TEXT NULL REFERENCES annal_versions(VersionId),
                CreatedAtUtc TEXT NOT NULL,
                CHECK ((SurvivorMemoryId IS NULL) = (SurvivorVersionId IS NULL)),
                CHECK (OutcomeCode <> 1 OR SurvivorVersionId IS NOT NULL),
                CHECK ((OutcomeCode = 1 AND ReasonCode = 0) OR (OutcomeCode = 2 AND ReasonCode BETWEEN 1 AND 10))
            );

            CREATE INDEX IF NOT EXISTS idx_long_rest_receipts_survivor_version
            ON long_rest_receipts(SurvivorVersionId);

            CREATE INDEX IF NOT EXISTS idx_long_rest_receipts_requested_survivor_version
            ON long_rest_receipts(RequestedSurvivorVersionId);
            """.ReplaceLineEndings("\n") + "\n",

        ["long_rest_suppressions_validate_insert"] =
            """
            -- One writer cannot bypass the exact-version, scope, lifecycle, or no-chain boundary of another.
            CREATE TRIGGER IF NOT EXISTS long_rest_suppressions_validate_insert
            BEFORE INSERT ON long_rest_suppressions
            WHEN NOT EXISTS (
                SELECT 1
                FROM annal_versions source
                JOIN annal_claims source_claim ON source_claim.ClaimId = source.ClaimId
                JOIN annal_heads source_head ON source_head.ClaimId = source.ClaimId
                    AND source_head.CurrentVersionId = source.VersionId
                JOIN saga_memories source_memory ON source_memory.Id = source_claim.SubjectId
                JOIN annal_versions survivor ON survivor.VersionId = NEW.SurvivorVersionId
                JOIN annal_claims survivor_claim ON survivor_claim.ClaimId = survivor.ClaimId
                JOIN annal_heads survivor_head ON survivor_head.ClaimId = survivor.ClaimId
                    AND survivor_head.CurrentVersionId = survivor.VersionId
                JOIN saga_memories survivor_memory ON survivor_memory.Id = survivor_claim.SubjectId
                JOIN long_rest_receipts receipt ON receipt.ReceiptId = NEW.ReceiptId
                WHERE source.VersionId = NEW.SourceVersionId
                  AND source_claim.SubjectStoreCode = 1
                  AND survivor_claim.SubjectStoreCode = 1
                  AND source.OperationCode <> 3 AND survivor.OperationCode <> 3
                  AND source.ScopeKindCode IN (1, 2)
                  AND source.ScopeKindCode = survivor.ScopeKindCode
                  AND source.CampaignId IS survivor.CampaignId
                  AND source_memory.ScopeKindCode = source.ScopeKindCode
                  AND source_memory.CampaignId IS source.CampaignId
                  AND survivor_memory.ScopeKindCode = survivor.ScopeKindCode
                  AND survivor_memory.CampaignId IS survivor.CampaignId
                  AND source.SensitivityCode = 0 AND survivor.SensitivityCode = 0
                  AND EXISTS (SELECT 1 FROM saga_memory_embeddings embedding WHERE embedding.MemoryId = survivor_memory.Id)
                  AND source_memory.PinnedAtUtc IS NULL AND survivor_memory.PinnedAtUtc IS NULL
                  AND source_memory.RetiredAtUtc IS NULL AND survivor_memory.RetiredAtUtc IS NULL
                  AND receipt.OutcomeCode = 1
                  AND (receipt.KindCode <> 1 OR (source.ContentHashFormatCode = survivor.ContentHashFormatCode
                      AND source.ContentHash = survivor.ContentHash))
                  AND receipt.SurvivorVersionId = survivor.VersionId
                  AND receipt.SurvivorMemoryId = survivor_claim.SubjectId
                  AND EXISTS (SELECT 1 FROM long_rest_receipt_inputs input
                      WHERE input.ReceiptId = receipt.ReceiptId
                        AND input.VersionId = source.VersionId AND input.MemoryId = source_claim.SubjectId)
                  AND EXISTS (SELECT 1 FROM long_rest_receipt_inputs input
                      WHERE input.ReceiptId = receipt.ReceiptId
                        AND input.VersionId = survivor.VersionId AND input.MemoryId = survivor_claim.SubjectId)
                  AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label
                      WHERE label.ArtifactKindCode = 6
                        AND lower(replace(label.ArtifactId, '-', '')) IN (
                            lower(replace(source_memory.Id, '-', '')), lower(replace(survivor_memory.Id, '-', ''))))
            )
            OR EXISTS (SELECT 1 FROM long_rest_suppressions WHERE SourceVersionId = NEW.SurvivorVersionId)
            OR EXISTS (SELECT 1 FROM long_rest_receipts
                WHERE OutcomeCode = 1 AND SurvivorVersionId = NEW.SourceVersionId)
            BEGIN
                SELECT RAISE(ABORT, 'Long Rest suppression requires compatible, current, unpinned Saga inputs and cannot form a chain.');
            END;
            """.ReplaceLineEndings("\n") + "\n",

        ["grimoire_utc_instant_columns"] =
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
            UNION ALL SELECT 'workspace_file_chunks' AS TableName, 'IndexedAt' AS ColumnName;
            """.ReplaceLineEndings("\n") + "\n",

    };

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CoreObjects
            .Where(static definition => PublishedObjects.Contains(definition.Name))
            .Select(static definition => Frozen.TryGetValue(definition.Name, out string? sql)
                ? definition with { Sql = sql }
                : definition),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 17,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps.Where(static step => step.ToVersion <= 17),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
