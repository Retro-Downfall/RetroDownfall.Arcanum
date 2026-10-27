using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact canonical version-6 source before normalized erasure indexes.</summary>
internal static class CovenantCanonicalSchemaVersionSixFixture
{
    internal const string PublishedFingerprint = "A57D21459F05BED41C7929D3AA2DED80EB32679AB817F2BC81ABC5D8798EC4A5";

    private const string Frozen0 =
        """
        CREATE TABLE IF NOT EXISTS "covenant_versions" (
            VersionId TEXT NOT NULL PRIMARY KEY,
            EntryId TEXT NOT NULL REFERENCES covenant_entries(EntryId),
            LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
            LaneRevision INTEGER NOT NULL CHECK (LaneRevision > 0),
            OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2)),
            AuthoredContent TEXT NULL,
            CompiledContent TEXT NULL,
            AuthoredHash BLOB NULL CHECK (AuthoredHash IS NULL OR length(AuthoredHash) = 32),
            RenderedHash BLOB NULL CHECK (RenderedHash IS NULL OR length(RenderedHash) = 32),
            CompiledByteCost INTEGER NOT NULL CHECK (CompiledByteCost >= 0),
            RequiredFenceLength INTEGER NOT NULL CHECK (RequiredFenceLength >= 0),
            CompilerPolicyVersion INTEGER NOT NULL CHECK (CompilerPolicyVersion > 0),
            RendererPolicyVersion INTEGER NOT NULL CHECK (RendererPolicyVersion > 0),
            OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3)),
            SourceTurnId TEXT NULL,
            SourceToolCallId TEXT NULL,
            BasePlanDigest BLOB NULL CHECK (BasePlanDigest IS NULL OR length(BasePlanDigest) = 32),
            AdmissionReceiptDigest BLOB NULL CHECK (AdmissionReceiptDigest IS NULL OR length(AdmissionReceiptDigest) = 32),
            WardReceiptDigest BLOB NULL CHECK (WardReceiptDigest IS NULL OR length(WardReceiptDigest) = 32),
            AuthorizationModeCode INTEGER NULL CHECK (AuthorizationModeCode IS NULL OR AuthorizationModeCode IN (1, 2, 3)),
            MutationId TEXT NOT NULL,
            RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
            AuthorizationDigest BLOB NOT NULL CHECK (length(AuthorizationDigest) = 32),
            FinalMutationDigest BLOB NOT NULL CHECK (length(FinalMutationDigest) = 32),
            PredecessorVersionId TEXT NULL REFERENCES "covenant_versions"(VersionId),
            AttachmentProvenanceCount INTEGER NOT NULL CHECK (AttachmentProvenanceCount >= 0),
            AttachmentProvenanceDigest BLOB NOT NULL CHECK (length(AttachmentProvenanceDigest) = 32),
            CreatedAtUtc TEXT NOT NULL,
            -- A Set carries content and both hashes; a Retire is a tombstone and carries neither. Storing
            -- one without the other would let a retirement resurrect text the operator asked to remove.
            CHECK (
                (OperationCode = 1
                    AND AuthoredContent IS NOT NULL
                    AND CompiledContent IS NOT NULL
                    AND AuthoredHash IS NOT NULL
                    AND RenderedHash IS NOT NULL)
                OR (OperationCode = 2
                    AND AuthoredContent IS NULL
                    AND CompiledContent IS NULL
                    AND AuthoredHash IS NULL
                    AND RenderedHash IS NULL
                    AND CompiledByteCost = 0)
            ),
            -- An agent proposal belongs to the Proposed lane and to nothing else. The rule runs one way:
            -- an operator and an approved agent retirement both reach either lane, because retiring a
            -- proposal is exactly how a proposal ends. Enforced here as well as in the contracts because a
            -- direct writer could otherwise seat proposed content on a Confirmed head, and the first
            -- complaint would be a constructor throw when the projection tried to load it.
            CHECK (OriginCode <> 2 OR LaneCode = 2),
            -- An agent-authored version names the turn it came from. The operator origin has no turn, so
            -- only the two agent origins require one. (The no-Global-Proposed rule spans entries and
            -- versions, so it cannot be a single-table CHECK; covenant_heads_validate_* enforces it.)
            CHECK (OriginCode = 1 OR SourceTurnId IS NOT NULL),
            -- New AgentApproved retirements are receipt-free. Historical AgentApproved rows retain their Ward
            -- evidence and its persisted authorization mode; no other origin can carry either field.
            CHECK (
                (OriginCode = 3 AND (
                    (WardReceiptDigest IS NULL AND AuthorizationModeCode IS NULL)
                    OR (WardReceiptDigest IS NOT NULL AND AuthorizationModeCode IS NOT NULL AND AuthorizationModeCode IN (2, 3))
                ))
                OR (OriginCode <> 3 AND WardReceiptDigest IS NULL AND AuthorizationModeCode IS NULL)
            ),
            -- An agent-authored version names the turn and tool call that produced it; an operator one does
            -- not have either, and must not borrow them.
            CHECK (
                (OriginCode IN (2, 3) AND SourceToolCallId IS NOT NULL AND BasePlanDigest IS NOT NULL)
                OR (OriginCode = 1 AND SourceToolCallId IS NULL AND BasePlanDigest IS NULL)
            ),
            -- Revision one is the first version in its lane and has no predecessor; every later revision
            -- links to exactly one.
            CHECK ((LaneRevision = 1 AND PredecessorVersionId IS NULL) OR (LaneRevision > 1 AND PredecessorVersionId IS NOT NULL))
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_entry_lane_revision
            ON covenant_versions(EntryId, LaneCode, LaneRevision);

        -- The candidate key covenant_heads carries a composite foreign key to. It proves a head's current
        -- version belongs to the same entry and lane and carries the same revision and operation, which no
        -- single-column reference to VersionId could.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_head_candidate
            ON covenant_versions(VersionId, EntryId, LaneCode, LaneRevision, OperationCode);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_mutation
            ON covenant_versions(MutationId);

        CREATE INDEX IF NOT EXISTS idx_covenant_versions_entry_created
            ON covenant_versions(EntryId, CreatedAtUtc);

        CREATE INDEX IF NOT EXISTS idx_covenant_versions_source_turn
            ON covenant_versions(SourceTurnId);
        """;

    private const string Frozen1 =
        """
        -- EntryId names the entry a receipt resolved, for an Applied and a NoChange outcome alike, so an entry
        -- erasure can find every receipt for its entry by that column. It is null only on receipts written
        -- before version 6. It sits where SQLite's ALTER TABLE ... ADD COLUMN splices it into the stored
        -- declaration, so a fresh installation and one the version-6 step evolved store the same text.
        CREATE TABLE IF NOT EXISTS covenant_mutation_receipts (
            MutationId TEXT NOT NULL PRIMARY KEY,
            RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
            AuthorizationDigest BLOB NOT NULL CHECK (length(AuthorizationDigest) = 32),
            FinalMutationDigest BLOB NOT NULL CHECK (length(FinalMutationDigest) = 32),
            MutationKindCode INTEGER NOT NULL CHECK (MutationKindCode IN (1, 2, 3, 4)),
            ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
            CampaignId TEXT NULL,
            TargetIdentityDigest BLOB NOT NULL CHECK (length(TargetIdentityDigest) = 32),
            LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
            OutcomeCode INTEGER NOT NULL CHECK (OutcomeCode IN (1, 2)),
            ResultingVersionId TEXT NULL,
            ResultingLaneRevision INTEGER NULL CHECK (ResultingLaneRevision IS NULL OR ResultingLaneRevision > 0),
            ResponseReceiptDigest BLOB NOT NULL CHECK (length(ResponseReceiptDigest) = 32),
            SourceTurnId TEXT NULL,
            CommittedAtUtc TEXT NOT NULL, EntryId TEXT NULL,
            CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
            -- An Applied mutation produced a version and revision; a NoChange one produced neither and must
            -- not borrow the previous head's identity as if it had.
            CHECK (
                (OutcomeCode = 1 AND ResultingVersionId IS NOT NULL AND ResultingLaneRevision IS NOT NULL)
                OR (OutcomeCode = 2 AND ResultingVersionId IS NULL AND ResultingLaneRevision IS NULL)
            )
        );

        CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_scope_quota
            ON covenant_mutation_receipts(ScopeCode, CampaignId, CommittedAtUtc);

        CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_source_turn
            ON covenant_mutation_receipts(SourceTurnId);

        CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_resulting_version
            ON covenant_mutation_receipts(ResultingVersionId);

        -- An entry erasure finds every receipt for its entry through this index.
        CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_entry
            ON covenant_mutation_receipts(EntryId);
        """;

    private const string Frozen2 =
        """
        CREATE TABLE IF NOT EXISTS covenant_version_attachment_provenance (
            VersionId TEXT NOT NULL REFERENCES covenant_versions(VersionId),
            Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
            -- Attachment, Session, and Entry identifiers are historical, not foreign keys. Deleting a source
            -- attachment must not erase durable provenance, matching the Saga and Lexicon rule; availability
            -- is resolved dynamically at read time instead.
            AttachmentId TEXT NOT NULL,
            AttachmentVersionIdentity TEXT NOT NULL,
            LogicalKey TEXT NOT NULL,
            ContentHash BLOB NOT NULL CHECK (length(ContentHash) = 32),
            SourceRangeKindCode INTEGER NOT NULL CHECK (SourceRangeKindCode IN (1, 2, 3)),
            SourceStart INTEGER NULL CHECK (SourceStart IS NULL OR SourceStart >= 0),
            SourceEnd INTEGER NULL CHECK (SourceEnd IS NULL OR SourceEnd >= 0),
            SourceTurnId TEXT NULL,
            MaterializationReference TEXT NULL,
            PRIMARY KEY (VersionId, Ordinal),
            -- WholeSource carries no offsets; a Utf16Range or ByteRange carries both, ordered. These are the
            -- immutable source coordinates, deliberately separate from provider-payload occurrence offsets.
            CHECK (
                (SourceRangeKindCode = 1 AND SourceStart IS NULL AND SourceEnd IS NULL)
                OR (SourceRangeKindCode IN (2, 3) AND SourceStart IS NOT NULL AND SourceEnd IS NOT NULL AND SourceEnd >= SourceStart)
            )
        );

        CREATE INDEX IF NOT EXISTS idx_covenant_version_attachment_provenance_attachment
            ON covenant_version_attachment_provenance(AttachmentId, AttachmentVersionIdentity);
        """;

    private const string Frozen3 =
        """
        CREATE TABLE IF NOT EXISTS covenant_review_events (
            Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (Sequence > 0),
            DatasetGeneration BLOB NOT NULL CHECK (length(DatasetGeneration) = 16),
            VersionId TEXT NOT NULL REFERENCES covenant_versions(VersionId) ON DELETE CASCADE,
            EntryId TEXT NOT NULL,
            LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
            OperationCode INTEGER NOT NULL CHECK (OperationCode IN (1, 2)),
            OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3)),
            ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
            CampaignId TEXT NULL,
            SourceTurnId TEXT NULL,
            SourceToolCallId TEXT NULL,
            UNIQUE (VersionId),
            CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL))
        );

        CREATE INDEX IF NOT EXISTS idx_covenant_review_events_global_queue
            ON covenant_review_events(ScopeCode, LaneCode, Sequence DESC) WHERE CampaignId IS NULL;

        CREATE INDEX IF NOT EXISTS idx_covenant_review_events_campaign_queue
            ON covenant_review_events(CampaignId, ScopeCode, LaneCode, Sequence DESC) WHERE CampaignId IS NOT NULL;
        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CovenantCanonicalObjects.Select(static definition => definition.Name switch
        {
            "covenant_versions" => definition with { Sql = Frozen0.ReplaceLineEndings("\n") + "\n" },

            "covenant_mutation_receipts" => definition with { Sql = Frozen1.ReplaceLineEndings("\n") + "\n" },

            "covenant_version_attachment_provenance" => definition with { Sql = Frozen2.ReplaceLineEndings("\n") + "\n" },

            "covenant_review_events" => definition with { Sql = Frozen3.ReplaceLineEndings("\n") + "\n" },

            _ => definition,
        }),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeRawSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(GrimoireSchemaFamily.Covenant,
                    GrimoireSchemaTransactionTier.CovenantCanonical, 6, Fingerprint, Objects),
                Objects,
                [.. GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical).Steps
                    .Where(static step => step.ToVersion <= 6)]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);
}
