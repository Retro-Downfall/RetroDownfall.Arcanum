using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact Core version-10 catalog before Lexicon curation changed its objects.</summary>
internal static class CoreSchemaVersionTenFixture
{
    internal const string PublishedFingerprint =
        "B484778B9288D99C4337FA3C95BEB56B95FDAE6B1D149C6A9A2A822591BBE951";

    private const string LexiconEntriesSql =
        """
        -- ScopeCampaignId is laid out the way SQLite lays out an added column rather than the way the columns
        -- above it are indented; see the same note in saga_memories.sql for why reindenting it reports
        -- DefinitionDrift on every installation evolved from version 1.
        --
        -- The empty string, not NULL, is the global scope. SQLite treats NULLs as distinct in a UNIQUE index,
        -- so a nullable scope column would let one global name be inserted any number of times and quietly
        -- undo the uniqueness this table has always had. NOT NULL DEFAULT '' also means the upgrade needs no
        -- sweep: every existing row is global the moment the column exists, which is exactly the behaviour
        -- preservation this change promises.
        CREATE TABLE IF NOT EXISTS lexicon_entries (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            NameNormalized TEXT NOT NULL,
            Type TEXT NOT NULL,
            FactsJson TEXT NOT NULL,
            FactsText TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        , ScopeCampaignId TEXT NOT NULL DEFAULT '');

        -- Scope first: every lookup knows which scope it is asking about before it knows the name, and the
        -- two-tier match reads one scope and then the other.
        CREATE UNIQUE INDEX IF NOT EXISTS IX_lexicon_entries_Scope_NameNormalized
        ON lexicon_entries(ScopeCampaignId, NameNormalized);

        """;

    private const string AnnalVersionsSql =
        """
        -- One immutable statement of one claim: who asserted it, whose memory it is, how sensitive it is, when
        -- it was true, and when Arcanum came to hold it.
        --
        -- Sequence is an INTEGER PRIMARY KEY, which is SQLite's rowid alias, so the engine allocates it inside
        -- the insert statement. An explicit MAX(Sequence) + 1 would race under the deferred transaction the
        -- Saga insert path opens, and the resulting unique-constraint abort is not a SQLITE_BUSY and would
        -- therefore not be retried. That allocation order is what annal_dependencies uses to make a cycle
        -- unrepresentable.
        --
        -- Transaction time has only one column. A version's belief ends at the RecordedAtUtc of the version
        -- whose PredecessorVersionId names it, and is open when none does. Storing that end would need an
        -- update to a row annal_versions_guard_update forbids updating, and would be a second measurement of a
        -- quantity the successor's own timestamp already states. Valid time keeps both ends, because a validity
        -- end is a fact the version states about the world rather than a consequence of a later write: a
        -- version may say "true until March" on the day it is written and never be superseded at all.
        CREATE TABLE IF NOT EXISTS annal_versions (
            Sequence INTEGER PRIMARY KEY,
            VersionId TEXT NOT NULL,
            ClaimId TEXT NOT NULL REFERENCES annal_claims(ClaimId),
            Revision INTEGER NOT NULL CHECK (Revision > 0),
            OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2, 3)),
            OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3, 4)),
            ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (0, 1, 2, 3)),
            CampaignId TEXT NULL,
            SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
            ContentHash BLOB NULL CHECK (ContentHash IS NULL OR length(ContentHash) = 32),
            ValidFromUtc TEXT NOT NULL,
            ValidToUtc TEXT NULL,
            RecordedAtUtc TEXT NOT NULL,
            PredecessorVersionId TEXT NULL REFERENCES annal_versions(VersionId) ON DELETE CASCADE,
            SourceSessionId TEXT NULL,
            -- A Campaign-scoped version names its Campaign, and no other kind borrows one. The two unresolved
            -- kinds are deliberately reachable here: a version that copies an unresolved subject's scope has to
            -- be able to say so rather than rounding it up to installation-global authority.
            CHECK ((ScopeKindCode = 2 AND CampaignId IS NOT NULL) OR (ScopeKindCode <> 2 AND CampaignId IS NULL)),
            -- A retirement is a tombstone and binds to no content. Letting one carry a hash would leave a record
            -- of exactly the bytes the retirement was meant to stop standing behind.
            CHECK ((OperationCode = 3 AND ContentHash IS NULL) OR (OperationCode <> 3 AND ContentHash IS NOT NULL)),
            -- Revision one begins a claim and has no predecessor; every later revision links to exactly one.
            CHECK ((Revision = 1 AND PredecessorVersionId IS NULL) OR (Revision > 1 AND PredecessorVersionId IS NOT NULL)),
            -- Both columns are round-trip "o"-format UTC text, which orders lexicographically, so this compares
            -- instants rather than a coincidence of formatting.
            CHECK (ValidToUtc IS NULL OR ValidToUtc >= ValidFromUtc),
            -- A version nobody attested cannot name a Session as its source.
            CHECK (OriginCode <> 4 OR SourceSessionId IS NULL)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_version
        ON annal_versions(VersionId);

        -- The candidate key annal_dependencies carries both of its composite foreign keys to. Binding an edge's
        -- recorded sequence to the version it names is what stops the ordering check from being told a lie.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_sequence_candidate
        ON annal_versions(VersionId, Sequence);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_claim_revision
        ON annal_versions(ClaimId, Revision);

        -- The candidate key annal_heads carries a composite foreign key to. A plain reference to VersionId would
        -- let a head adopt a version belonging to another claim, or one whose revision and operation disagree
        -- with the head's own columns.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_head_candidate
        ON annal_versions(VersionId, ClaimId, Revision, OperationCode);

        -- Reading one claim's history in order, which is the shape every consumer of this table wants.
        CREATE INDEX IF NOT EXISTS idx_annal_versions_claim_recorded
        ON annal_versions(ClaimId, RecordedAtUtc);

        -- The derived transaction-time end resolves a version's successor through this column, and an erasure
        -- walks the same edge.
        CREATE INDEX IF NOT EXISTS idx_annal_versions_predecessor
        ON annal_versions(PredecessorVersionId);

        """;

    private const string LexiconInsertSql =
        """
        CREATE TRIGGER IF NOT EXISTS lexicon_entries_ai
        AFTER INSERT ON lexicon_entries
        BEGIN
            INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
            VALUES (new.rowid, new.Name, new.Type, new.FactsText);
        END;

        """;

    private const string LexiconDeleteSql =
        """
        CREATE TRIGGER IF NOT EXISTS lexicon_entries_ad
        AFTER DELETE ON lexicon_entries
        BEGIN
            INSERT INTO lexicon_fts(lexicon_fts, rowid, Name, Type, FactsText)
            VALUES ('delete', old.rowid, old.Name, old.Type, old.FactsText);
        END;

        """;

    private const string LexiconUpdateSql =
        """
        CREATE TRIGGER IF NOT EXISTS lexicon_entries_au
        AFTER UPDATE ON lexicon_entries
        BEGIN
            INSERT INTO lexicon_fts(lexicon_fts, rowid, Name, Type, FactsText)
            VALUES ('delete', old.rowid, old.Name, old.Type, old.FactsText);

            INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
            VALUES (new.rowid, new.Name, new.Type, new.FactsText);
        END;

        """;

    private const string UtcInstantColumnsSql =
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

    // Although version 10 normalizes comments, the fixtures for versions 1-5 still hash raw bytes.
    private const string LexiconFtsSql =
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS lexicon_fts USING fts5(
            Name,
            Type,
            FactsText,
            content='lexicon_entries',
            content_rowid='rowid'
        );

        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. CoreSchemaVersionTwelveFixture.Objects
            .Where(static definition => !definition.Name.StartsWith("annal_review_", StringComparison.Ordinal))
            .Where(static definition => definition.Name != "lexicon_annal_fact_provenance")
            .Select(static definition => definition.Name switch
            {
                "lexicon_entries" => definition with { Sql = LexiconEntriesSql.ReplaceLineEndings("\n") },

                "annal_versions" => definition with { Sql = AnnalVersionsSql.ReplaceLineEndings("\n") },

                "lexicon_entries_ai" => definition with { Sql = LexiconInsertSql.ReplaceLineEndings("\n") },

                "lexicon_entries_ad" => definition with { Sql = LexiconDeleteSql.ReplaceLineEndings("\n") },

                "lexicon_entries_au" => definition with { Sql = LexiconUpdateSql.ReplaceLineEndings("\n") },

                "grimoire_utc_instant_columns" => definition with { Sql = UtcInstantColumnsSql.ReplaceLineEndings("\n") },

                "lexicon_fts" => definition with { Sql = LexiconFtsSql.ReplaceLineEndings("\n") },

                _ => definition,
            }),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Core,
                    GrimoireSchemaTransactionTier.Core,
                    version: 10,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.Core)
                        .Steps
                        .Where(static step => step.ToVersion <= 10),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
