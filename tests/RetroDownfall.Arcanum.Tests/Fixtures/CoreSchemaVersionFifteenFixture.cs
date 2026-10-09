using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact Core version-15 tree before Campaign rollup storage was added.</summary>
internal static class CoreSchemaVersionFifteenFixture
{
    internal const string PublishedFingerprint =
        "24765BDDB071B091B7DE145F23A87FBEF5E1D3487D71B835C139B9C4067AC4E6";

    private const string ArtifactSensitivitySql =
        """
        -- The core, content-free information-flow ledger. A row records that one artifact is Covenant
        -- derived and where that taint came from, so reset, purge, and cache filters can find every tainted
        -- sink without reopening the artifact. Only a tainted artifact carries a label: an untainted one has
        -- no row at all, which is why exact provenance requires at least one generation rather than zero.
        -- Session, Campaign, and turn are historical owner identities without foreign keys, because a label
        -- outlives the turn that produced it and is retired through the owner-deletion journal.
        CREATE TABLE IF NOT EXISTS artifact_sensitivity (
            LabelId TEXT NOT NULL PRIMARY KEY,
            ArtifactKindCode INTEGER NOT NULL CHECK (ArtifactKindCode IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13)),
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
        """;

    private const string UtcInventoryViewSql =
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
        UNION ALL SELECT 'campaign_path_identities' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_path_marker_intents' AS TableName, 'CreatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_path_marker_intents' AS TableName, 'UpdatedAtUtc' AS ColumnName
        UNION ALL SELECT 'campaign_path_operation_receipts' AS TableName, 'CompletedAtUtc' AS ColumnName
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
        ;
        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionSixteenFixture.Objects
            .Where(static definition => !IsVersionSixteenObject(definition.Name))
            .Select(static definition => definition.Name switch
            {
                "artifact_sensitivity" => definition with { Sql = ArtifactSensitivitySql.ReplaceLineEndings("\n") + "\n" },

                "grimoire_utc_instant_columns" => definition with { Sql = UtcInventoryViewSql.ReplaceLineEndings("\n") + "\n" },

                _ => definition,
            }),
    ];

    private static bool IsVersionSixteenObject(string name) =>
        name.StartsWith("campaign_rollup_", StringComparison.Ordinal)
        || name.StartsWith("campaign_contribution_", StringComparison.Ordinal)
        || name.StartsWith("campaign_fork_frontiers", StringComparison.Ordinal)
        || name.StartsWith("campaign_maintenance_checkpoints", StringComparison.Ordinal)
        || name.StartsWith("Entries_campaign_contribution_", StringComparison.Ordinal)
        || name.StartsWith("artifact_sensitivity_campaign_contribution_", StringComparison.Ordinal);

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 15,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps.Where(static step => step.ToVersion <= 15),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
