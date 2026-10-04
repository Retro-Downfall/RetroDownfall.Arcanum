using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>The exact raw Covenant canonical version-5 tree before entry erasure was added.</summary>
/// <remarks>
/// Version 6 gives <c>covenant_key_epochs</c> its binding epoch and <c>covenant_mutation_receipts</c> its
/// entry column, admits entry erasure in seven delete guards, rewrites the three key-epoch triggers on
/// <c>covenant_heads</c>, and adds a binding-epoch guard and two delete guards. This tier still publishes
/// the raw computation, so every object version 6 edits is frozen here byte for byte, comments included,
/// and the older canonical fixtures inherit the frozen text through this one.
/// </remarks>
internal static class CovenantCanonicalSchemaVersionFiveFixture
{
    internal const string PublishedFingerprint =
        "E4C4284B895BBBE50515D18FAC6066348D73C3A7D166F434B96BA675697DA925";

    /// <summary>The objects version 6 introduced, which version 5 did not have.</summary>
    private static readonly string[] AddedAtSix =
    [
        "covenant_key_epochs_guard_incarnation",
        "covenant_key_epochs_guard_delete",
        "covenant_curation_heads_guard_delete",
    ];

    // Version 6 appends the binding epoch column and its leading comment.
    private const string CovenantKeyEpochsSql =
        """
        CREATE TABLE IF NOT EXISTS covenant_key_epochs (
            -- Keyed by normalized key alone, across every Global and Campaign lane. Rows are therefore
            -- proportional to retained canonical keys rather than to historical Campaign churn, which keeps
            -- Global effect validation O(1) without an unbounded tombstone table or a scan under the write
            -- lock.
            NormalizedKey TEXT NOT NULL PRIMARY KEY CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
            KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch > 0),
            UpdatedAtUtc TEXT NOT NULL
        );
        """;

    // Version 6 splices in the entry column and appends its index.
    private const string CovenantMutationReceiptsSql =
        """
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
            CommittedAtUtc TEXT NOT NULL,
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
        """;

    // Version 6 rewrites the KeyEpoch paragraph: the column now stores the binding epoch.
    private const string CovenantCurationHeadsSql =
        """
        -- The guarded current pointer for one curation subject. The subject is a scoped key and lane, and it
        -- deliberately does not require a row in covenant_heads: masking a Global key inside a Campaign is
        -- exactly the case where that Campaign holds no entry, no head, and no version for the key.
        --
        -- KeyEpoch is part of the subject rather than a recorded detail. A key that is retired, reclaimed,
        -- and later re-created is a different key wearing an old name, and a pin recorded against the earlier
        -- epoch must be inert rather than silently applying to content the operator never saw.
        --
        -- NOTE: this file is the head definition and its statements are copied character for character into
        -- the version-2 transition files.
        CREATE TABLE IF NOT EXISTS covenant_curation_heads (
            ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
            CampaignId TEXT NULL,
            NormalizedKey TEXT NOT NULL CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
            LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
            KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch >= 0),
            IsPinned INTEGER NOT NULL CHECK (IsPinned IN (0, 1)),
            IsMasked INTEGER NOT NULL CHECK (IsMasked IN (0, 1)),
            CurrentVersionId TEXT NOT NULL,
            CurrentRevision INTEGER NOT NULL CHECK (CurrentRevision > 0),
            UpdatedAtUtc TEXT NOT NULL,
            -- The composite reference is the point: a plain reference to CurationVersionId would let a head
            -- adopt a version belonging to another subject, or one whose revision disagrees with its own.
            FOREIGN KEY (CurrentVersionId, NormalizedKey, LaneCode, KeyEpoch, CurrentRevision)
                REFERENCES covenant_curation_versions(CurationVersionId, NormalizedKey, LaneCode, KeyEpoch, Revision),
            CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
            -- Only a Campaign Confirmed subject can be masked, on the same terms the version table states.
            CHECK (IsMasked = 0 OR (ScopeCode = 2 AND LaneCode = 1))
        );

        -- A NULL inside a SQLite primary key does not enforce uniqueness, so the subject's identity is two
        -- partial unique indexes, exactly as covenant_entries keys its own nullable Campaign.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_heads_global_subject
            ON covenant_curation_heads(NormalizedKey, LaneCode, KeyEpoch) WHERE CampaignId IS NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_heads_campaign_subject
            ON covenant_curation_heads(CampaignId, NormalizedKey, LaneCode, KeyEpoch) WHERE CampaignId IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_curation_heads_current_version
            ON covenant_curation_heads(CurrentVersionId);

        -- The turn snapshot reads one Campaign's live masks through this index, so it leads with CampaignId.
        CREATE INDEX IF NOT EXISTS idx_covenant_curation_heads_campaign_masks
            ON covenant_curation_heads(CampaignId, IsMasked, NormalizedKey);
        """;

    // Version 6 admits entry erasure in this guard and the six that follow it.
    private const string CovenantEntriesGuardDeleteSql =
        """
        -- Entries leave only through owner cleanup or Covenant family maintenance, which remove the entry
        -- together with its versions, heads, and provenance in one transaction. An unscoped delete would
        -- strand that history. Both authorizations begin FALSE on every connection, so ordinary application
        -- work reaches this guard and aborts.
        CREATE TRIGGER IF NOT EXISTS covenant_entries_guard_delete
        BEFORE DELETE ON covenant_entries
        WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
        BEGIN
            SELECT RAISE(ABORT, 'covenant_entries delete requires an authorized cleanup scope.');
        END;
        """;

    private const string CovenantVersionsGuardDeleteSql =
        """
        -- Versions are removed only by owner cleanup or Covenant family maintenance, which take the whole
        -- chain and everything pointing at it in one transaction. Deleting one version outside that scope
        -- would break the predecessor chain and orphan the head that still names it. Both authorizations
        -- begin FALSE on every connection, so ordinary application work reaches this guard and aborts.
        CREATE TRIGGER IF NOT EXISTS covenant_versions_guard_delete
        BEFORE DELETE ON covenant_versions
        WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
        BEGIN
            SELECT RAISE(ABORT, 'covenant_versions delete requires an authorized cleanup scope.');
        END;
        """;

    private const string CovenantVersionAttachmentProvenanceGuardDeleteSql =
        """
        -- Provenance is deleted only alongside the version that owns it, under owner cleanup or Covenant
        -- family maintenance. A standalone delete would leave the version's recorded provenance count and
        -- digest describing rows that are no longer present. Both authorizations begin FALSE on every
        -- connection, so ordinary application work reaches this guard and aborts.
        CREATE TRIGGER IF NOT EXISTS covenant_version_attachment_provenance_guard_delete
        BEFORE DELETE ON covenant_version_attachment_provenance
        WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
        BEGIN
            SELECT RAISE(ABORT, 'covenant_version_attachment_provenance delete requires an authorized cleanup scope.');
        END;
        """;

    private const string CovenantMutationReceiptsGuardDeleteSql =
        """
        -- Receipts are retained so a repeated request is recognized rather than applied twice, and they also
        -- carry the per-scope quota history. They are removed only when their owner is being cleaned up or
        -- the Covenant family is being torn down. Both authorizations begin FALSE on every connection, so
        -- ordinary application work reaches this guard and aborts.
        CREATE TRIGGER IF NOT EXISTS covenant_mutation_receipts_guard_delete
        BEFORE DELETE ON covenant_mutation_receipts
        WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
        BEGIN
            SELECT RAISE(ABORT, 'covenant_mutation_receipts delete requires an authorized cleanup scope.');
        END;
        """;

    private const string CovenantCurationVersionsGuardDeleteSql =
        """
        -- Curation versions are removed only by owner cleanup or Covenant family maintenance, which take the
        -- whole chain and everything pointing at it in one transaction. Deleting one version outside that
        -- scope would break the predecessor chain and orphan the head that still names it. Both
        -- authorizations begin FALSE on every connection, so ordinary application work reaches this guard.
        CREATE TRIGGER IF NOT EXISTS covenant_curation_versions_guard_delete
        BEFORE DELETE ON covenant_curation_versions
        WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
        BEGIN
            SELECT RAISE(ABORT, 'covenant_curation_versions delete requires an authorized cleanup scope.');
        END;
        """;

    private const string CovenantCurationReceiptsGuardDeleteSql =
        """
        -- A curation receipt outlives the change it describes for the same reason a mutation receipt does: it
        -- is the answer a replay resolves through. Removing one is authorized cleanup work or nothing.
        CREATE TRIGGER IF NOT EXISTS covenant_curation_receipts_guard_delete
        BEFORE DELETE ON covenant_curation_receipts
        WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
        BEGIN
            SELECT RAISE(ABORT, 'covenant_curation_receipts delete requires an authorized cleanup scope.');
        END;
        """;

    private const string CovenantSearchOutboxGuardDeleteSql =
        """
        -- The outbox is the accelerator's work queue. A row leaves it only when the synchronization worker
        -- has applied it, in the same transaction that advances the applied FTS tuple, or when family
        -- maintenance is tearing the dataset down. Any other delete would drop a projection delta while the
        -- applied tuple still claims the sequence was published, and search would keep serving text that
        -- canonical no longer holds. Both authorizations begin FALSE on every connection, so ordinary
        -- application work aborts here.
        CREATE TRIGGER IF NOT EXISTS covenant_search_outbox_guard_delete
        BEFORE DELETE ON covenant_search_outbox
        WHEN arcanum_accelerator_sync_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
        BEGIN
            SELECT RAISE(ABORT, 'covenant_search_outbox delete requires an authorized synchronization scope.');
        END;
        """;

    // Version 6 stamps a new key row with binding epoch 0 in this trigger and the two that follow it.
    private const string CovenantHeadsKeyEpochInsertSql =
        """
        -- Global effect validation compares a recorded epoch for a normalized key instead of rescanning
        -- every Campaign head that shares it, so the epoch has to move whenever a head for that key appears.
        -- The counter is keyed by normalized key alone, so one bump covers every scope and lane using it,
        -- and the first head to claim a key starts the counter at one.
        CREATE TRIGGER IF NOT EXISTS covenant_heads_key_epoch_insert
        AFTER INSERT ON covenant_heads
        BEGIN
            INSERT INTO covenant_key_epochs(NormalizedKey, KeyEpoch, UpdatedAtUtc)
            VALUES (NEW.NormalizedKey, 1, NEW.UpdatedAtUtc)
            ON CONFLICT(NormalizedKey) DO UPDATE SET
                KeyEpoch = KeyEpoch + 1,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
        END;
        """;

    private const string CovenantHeadsKeyEpochUpdateSql =
        """
        -- Advancing a head, retiring it, and reactivating it are all changes to what that normalized key
        -- resolves to, so each one moves the key's dependency epoch exactly as an insert does. A validator
        -- holding an earlier epoch must fail its comparison rather than trust a resolution taken before the
        -- head moved. The insert branch covers the case where the epoch row was reclaimed while heads for
        -- the key still exist.
        CREATE TRIGGER IF NOT EXISTS covenant_heads_key_epoch_update
        AFTER UPDATE ON covenant_heads
        BEGIN
            INSERT INTO covenant_key_epochs(NormalizedKey, KeyEpoch, UpdatedAtUtc)
            VALUES (NEW.NormalizedKey, 1, NEW.UpdatedAtUtc)
            ON CONFLICT(NormalizedKey) DO UPDATE SET
                KeyEpoch = KeyEpoch + 1,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
        END;
        """;

    private const string CovenantHeadsKeyEpochDeleteSql =
        """
        -- Removing a head changes that key's resolution as much as adding one does, and Campaign cleanup
        -- removes heads in bulk, which is exactly when a validator is most likely to be holding a stale
        -- resolution. The epoch row may already have been reclaimed for this key, so the same upsert shape
        -- is used rather than a bare update, keyed on the removed row's key and timestamp.
        CREATE TRIGGER IF NOT EXISTS covenant_heads_key_epoch_delete
        AFTER DELETE ON covenant_heads
        BEGIN
            INSERT INTO covenant_key_epochs(NormalizedKey, KeyEpoch, UpdatedAtUtc)
            VALUES (OLD.NormalizedKey, 1, OLD.UpdatedAtUtc)
            ON CONFLICT(NormalizedKey) DO UPDATE SET
                KeyEpoch = KeyEpoch + 1,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
        END;
        """;

    internal static IReadOnlyList<GrimoireSchemaObject> Objects =>
    [
        .. GrimoireSchemaCatalog.CovenantCanonicalObjects
            .Where(static definition => !AddedAtSix.Contains(definition.Name))
            .Select(static definition => definition.Name switch
            {
                "covenant_key_epochs" => Frozen(definition, CovenantKeyEpochsSql),

                "covenant_mutation_receipts" => Frozen(definition, CovenantMutationReceiptsSql),

                "covenant_curation_heads" => Frozen(definition, CovenantCurationHeadsSql),

                "covenant_entries_guard_delete" => Frozen(definition, CovenantEntriesGuardDeleteSql),

                "covenant_versions_guard_delete" => Frozen(definition, CovenantVersionsGuardDeleteSql),

                "covenant_version_attachment_provenance_guard_delete" =>
                    Frozen(definition, CovenantVersionAttachmentProvenanceGuardDeleteSql),

                "covenant_mutation_receipts_guard_delete" => Frozen(definition, CovenantMutationReceiptsGuardDeleteSql),

                "covenant_curation_versions_guard_delete" => Frozen(definition, CovenantCurationVersionsGuardDeleteSql),

                "covenant_curation_receipts_guard_delete" => Frozen(definition, CovenantCurationReceiptsGuardDeleteSql),

                "covenant_search_outbox_guard_delete" => Frozen(definition, CovenantSearchOutboxGuardDeleteSql),

                "covenant_heads_key_epoch_insert" => Frozen(definition, CovenantHeadsKeyEpochInsertSql),

                "covenant_heads_key_epoch_update" => Frozen(definition, CovenantHeadsKeyEpochUpdateSql),

                "covenant_heads_key_epoch_delete" => Frozen(definition, CovenantHeadsKeyEpochDeleteSql),

                _ => definition,
            }),
    ];

    internal static string Fingerprint => GrimoireSchemaCatalog.ComputeRawSourceFingerprint(Objects);

    internal static GrimoireSchemaVersionChainSet ChainSet() =>
        new(
        [
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core),
            new GrimoireSchemaVersionChain(
                GrimoireSchemaManifestBuilder.Build(
                    GrimoireSchemaFamily.Covenant,
                    GrimoireSchemaTransactionTier.CovenantCanonical,
                    version: 5,
                    Fingerprint,
                    Objects),
                Objects,
                [
                    .. GrimoireSchemaVersionChains.Default
                        .ForTier(GrimoireSchemaTransactionTier.CovenantCanonical)
                        .Steps
                        .Where(static step => step.ToVersion <= 5),
                ]),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

    private static GrimoireSchemaObject Frozen(GrimoireSchemaObject definition, string text) =>
        definition with { Sql = text.ReplaceLineEndings("\n") + "\n" };
}
