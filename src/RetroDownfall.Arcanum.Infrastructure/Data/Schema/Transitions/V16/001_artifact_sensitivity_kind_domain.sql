DROP TRIGGER IF EXISTS Sessions_Id_guard_identity_insert;
DROP TRIGGER IF EXISTS Sessions_Id_guard_identity_update;
DROP TRIGGER IF EXISTS artifact_sensitivity_SessionId_guard_identity_insert;
DROP TRIGGER IF EXISTS artifact_sensitivity_campaign_contribution_delete;
DROP TRIGGER IF EXISTS artifact_sensitivity_campaign_contribution_insert;
DROP TRIGGER IF EXISTS artifact_sensitivity_guard_delete;
DROP TRIGGER IF EXISTS artifact_sensitivity_guard_update;
DROP TRIGGER IF EXISTS local_erasure_work_items_guard_insert;
DROP TRIGGER IF EXISTS managed_file_write_intents_guard_delete;
DROP TRIGGER IF EXISTS managed_file_write_intents_guard_update;
CREATE TABLE artifact_sensitivity_v16 (
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
INSERT INTO artifact_sensitivity_v16 SELECT * FROM artifact_sensitivity;
DROP TABLE artifact_sensitivity;
ALTER TABLE artifact_sensitivity_v16 RENAME TO "artifact_sensitivity";


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

-- The write-time half of the identity canonicalisation, and the half that makes it hold. Once every
-- stored identity has one spelling a comparison can be an exact indexed equality again, and the only
-- thing that keeps it that way is a refusal at the write. A guard fires on whatever produced the row -
-- the object-relational writer, a raw Guid handed to the provider, an interpolation, or SQL nobody has
-- written yet - which is exactly what a source scan of the writers can never cover.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong. A dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence; the two columns that legitimately hold that form - lexicon_entries.Id
-- and IdempotencyClaims.Id - are single-writer and are deliberately guarded nowhere. The predicate here
-- is the one IdentitySpellingBackfill.CountNonCanonicalAsync asks of the stored data, so the question a
-- guard refuses on and the question the sweep reports on are the same question.
--
-- ONE TRIGGER PER COLUMN, not one per table, and the reasons are in this order of weight. RAISE(ABORT)
-- takes a string literal, so a trigger covering several columns structurally cannot name the one that
-- failed - and the message is the whole of what a developer sees. The update half has to be
-- BEFORE UPDATE OF <column>, which is per-column by construction. And a guarded table can carry
-- identity-shaped columns that are deliberately outside this family - the provenance SessionIds,
-- lexicon_fact_attachment_provenance.EntryId, attachment_memory_consultations.SourceEntryId - so a name
-- of the form <table>_guard_identity would claim a coverage the trigger does not have. The cost is an
-- object per governed column where one per table would do, paid once, in a tree that is one object per
-- file.
--
-- The authoritative list of what this family governs is IdentitySpellingBackfill.VerifiedColumns plus
-- artifact_sensitivity.SessionId, and a test pins that every entry has its guards.
--
-- Sessions."Id" is the identity most of this schema is keyed to. Every writer hands the provider a Guid
-- or an already-uppercased rendering of one - the object-relational writer, the protected artifact
-- transfer store and the backup session importer - so every row an installation holds is already
-- canonical, and this guard is here for the writer nobody has written yet.
CREATE TRIGGER IF NOT EXISTS Sessions_Id_guard_identity_insert
BEFORE INSERT ON "Sessions"
WHEN NEW."Id" IS NOT NULL
    AND (NEW."Id" <> upper(NEW."Id")
        OR length(NEW."Id") <> 36
        OR substr(NEW."Id", 9, 1) <> '-'
        OR substr(NEW."Id", 14, 1) <> '-'
        OR substr(NEW."Id", 19, 1) <> '-'
        OR substr(NEW."Id", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'Sessions.Id must be stored as an uppercase dashed 36-character identity.');
END;

-- The update half of the identity guards, and BEFORE UPDATE OF <column> rather than BEFORE UPDATE is
-- load-bearing rather than a narrowing for speed.
--
-- A guard refuses the value being written. On an UPDATE the values being written are the ones named in
-- the SET clause, and a trigger that also judged the columns the statement leaves alone would refuse a
-- row for data that was already there. That is not hypothetical: the version-5 step installs these
-- triggers before it runs its own sweep, and that sweep repairs one identity column of a row at a time -
-- SessionAttachments."Id" in a bounded page, then "SessionId" and "EntryId" against the identities they
-- name. A guard that judged all three on every update would abort the migration on any installation that
-- has ever held an attachment, and every retry of it, leaving the tier permanently unable to reach head.
-- UPDATE OF removes that by construction rather than by an OLD-value comparison a copier could omit.
--
-- The sweep's own writes pass for a second reason, and both are needed: it only ever selects a row whose
-- shape is already canonical, so the value it writes is upper() of a 36-character dashed string. See
-- IdentitySpellingBackfill.CanonicalShapeClause.
--
-- Where a table already refuses every update whatever it changes - assistant_entry_finalizations and
-- artifact_sensitivity both do - no update guard of this family is added, because it could never be
-- reached. That is a per-table finding rather than a general rule.
--
-- Nothing in the shipped code updates Sessions."Id" and nothing should: eight of its fourteen
-- foreign-key children refuse the write by trigger, four of them unconditionally, which is why version 5
-- verifies this column and never moves it.
CREATE TRIGGER IF NOT EXISTS Sessions_Id_guard_identity_update
BEFORE UPDATE OF "Id" ON "Sessions"
WHEN NEW."Id" IS NOT NULL
    AND (NEW."Id" <> upper(NEW."Id")
        OR length(NEW."Id") <> 36
        OR substr(NEW."Id", 9, 1) <> '-'
        OR substr(NEW."Id", 14, 1) <> '-'
        OR substr(NEW."Id", 19, 1) <> '-'
        OR substr(NEW."Id", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'Sessions.Id must be stored as an uppercase dashed 36-character identity.');
END;

-- artifact_sensitivity.SessionId records the Session a Covenant-derived artifact label belongs to,
-- written by the artifact sensitivity ledger. It is the one column of this family the version-5 sweep
-- does not count: it is left to this guard, which answers for every future write rather than for one
-- moment.
--
-- Nullable - an artifact need not belong to a Session - so the guard says nothing about a NULL.
--
-- Insert only, and that is a finding about this table rather than a general rule:
-- artifact_sensitivity_guard_update aborts every update to it whatever the update changes, because a
-- label is immutable evidence about one exact artifact revision. An update-time identity check here could
-- never be reached.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_SessionId_guard_identity_insert
BEFORE INSERT ON artifact_sensitivity
WHEN NEW.SessionId IS NOT NULL
    AND (NEW.SessionId <> upper(NEW.SessionId)
        OR length(NEW.SessionId) <> 36
        OR substr(NEW.SessionId, 9, 1) <> '-'
        OR substr(NEW.SessionId, 14, 1) <> '-'
        OR substr(NEW.SessionId, 19, 1) <> '-'
        OR substr(NEW.SessionId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'artifact_sensitivity.SessionId must be stored as an uppercase dashed 36-character identity.');
END;

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

-- A label is the immutable evidence that one exact artifact revision is Covenant derived. Editing it
-- in place is how a downgrade would happen: the sensitivity code, the generation provenance, or the
-- content digest could be rewritten to describe cleaner bytes than the artifact actually holds,
-- and every reader that trusts the label would then trust the artifact. A changed artifact gets a
-- new label written beside its new revision instead.
CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_guard_update
BEFORE UPDATE ON artifact_sensitivity
BEGIN
    SELECT RAISE(ABORT, 'artifact_sensitivity rows are immutable; a new artifact revision needs a new label.');
END;

-- Creating a work item is what grants the authority to delete a file. The authority is not taken
-- from the caller: this guard requires that an AdoptedAndLabeled producer row still exists at the
-- exact revision, artifact, and label the caller claims to have read, so the location and ownership
-- being copied come from a row Arcanum wrote when it created the file. A caller that supplies its own
-- root, leaf, or ownership values cannot get past this check.
--
-- Managed-write authorization is explicitly refused. That scope belongs to the writer, and letting it
-- also open erasure work would make one borrowed scope able to both create and destroy.
CREATE TRIGGER IF NOT EXISTS local_erasure_work_items_guard_insert
BEFORE INSERT ON local_erasure_work_items
BEGIN
    SELECT RAISE(ABORT, 'A local erasure work item insert requires retention-purge or family-maintenance authorization.')
    WHERE arcanum_sensitivity_purge_authorized() = 0
        AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'A local erasure work item never accepts managed-write or restore-staging authorization.')
    WHERE arcanum_managed_file_intent_mutation_authorized() = 1
        OR arcanum_restore_staging_managed_authority_sanitization_authorized() = 1;

    SELECT RAISE(ABORT, 'A local erasure work item is created Prepared, at revision zero, with no deletion evidence.')
    WHERE NEW.StateCode <> 1
        OR NEW.CheckpointRevision <> 0
        OR NEW.DeletionEvidenceCode IS NOT NULL;

    SELECT RAISE(ABORT, 'A local erasure work item requires its exact AdoptedAndLabeled producer row at the expected revision.')
    WHERE NOT EXISTS (
        SELECT 1
        FROM managed_file_write_intents
        WHERE WriteOperationId = NEW.SourceWriteOperationId
            AND PhaseCode = 7
            AND Revision = NEW.ExpectedSourceRevision
            AND ArtifactId = NEW.ArtifactId
            AND SensitivityLabelId = NEW.SourceSensitivityLabelId
    );

    SELECT RAISE(ABORT, 'A local erasure work item requires the exact live sensitivity label of its producer.')
    WHERE NOT EXISTS (
        SELECT 1
        FROM artifact_sensitivity
        WHERE LabelId = NEW.SourceSensitivityLabelId
            AND ArtifactId = NEW.ArtifactId
    );
END;

-- Two disjoint branches, written as one rule so a caller cannot hold both scopes and satisfy the
-- weaker one. Ordinary retention removes only a row whose file question is settled: Cleaned,
-- ManualNonrevocable, or Erased. An AdoptedAndLabeled row still owns a real file and a real label, so
-- retaining it away would leave a labelled sensitive file with nothing in the database admitting to
-- owning it.
--
-- The restore-staging branch removes a row at any phase, because a restored row describes a file on a
-- machine this installation is not. It requires the exact immutable tombstone first, no local erasure
-- row still pointing at this producer, and a label disposition consistent with the phase: an adopted
-- source must have had its exact label removed in the same transaction, and every other phase must
-- have had no live label at all.
CREATE TRIGGER IF NOT EXISTS managed_file_write_intents_guard_delete
BEFORE DELETE ON managed_file_write_intents
BEGIN
    SELECT RAISE(ABORT, 'A managed write intent delete requires either terminal retention authorization or its exact restore-staging tombstone.')
    WHERE NOT (
        (
            arcanum_restore_staging_managed_authority_sanitization_authorized() = 0
            AND OLD.PhaseCode IN (8, 9, 10)
            AND (
                arcanum_sensitivity_purge_authorized() = 1
                OR arcanum_covenant_family_maintenance_authorized() = 1
                OR arcanum_owner_cleanup_authorized() = 1
            )
        )
        OR (
            arcanum_restore_staging_managed_authority_sanitization_authorized() = 1
            AND arcanum_sensitivity_purge_authorized() = 0
            AND arcanum_covenant_family_maintenance_authorized() = 0
            AND arcanum_owner_cleanup_authorized() = 0
            AND arcanum_managed_file_intent_mutation_authorized() = 0
            AND NOT EXISTS (
                SELECT 1
                FROM local_erasure_work_items
                WHERE SourceWriteOperationId = OLD.WriteOperationId
            )
            AND NOT EXISTS (
                SELECT 1
                FROM artifact_sensitivity
                WHERE LabelId = OLD.SensitivityLabelId
            )
            AND EXISTS (
                SELECT 1
                FROM restored_managed_file_authority_tombstones
                WHERE SourceKind = 1
                    AND SourceRowId = OLD.WriteOperationId
                    AND SourceWriteOperationId = OLD.WriteOperationId
                    AND ArtifactId = OLD.ArtifactId
                    AND SensitivityLabelId = OLD.SensitivityLabelId
                    AND OriginalStateCode = OLD.PhaseCode
                    AND (
                        (LabelDispositionCode = 2 AND OLD.PhaseCode = 7)
                        OR (LabelDispositionCode = 1 AND OLD.PhaseCode <> 7)
                    )
            )
        )
    );
END;

-- This trigger selects its authorization by the exact phase edge, which is unusual and deliberate.
-- Every writer and write-recovery edge belongs to the managed-file scope, and both erasure scopes
-- must be closed while it runs. The single AdoptedAndLabeled to Erased edge is the opposite: it is
-- the moment ownership ends rather than begins, so it refuses the managed-file scope and requires
-- exactly one erasure scope. Requiring exactly one, rather than at least one, means a caller cannot
-- hold both and let the trigger pick whichever explanation fits.
--
-- The evidence rules are one-time fills. CreatedChildPhysicalIdentityDigest is the only proof that a
-- child on disk was created by this operation, so it is written once on Prepared to TempCreated and
-- is otherwise byte-identical across every edge, including the terminal ones. FinalOwnershipEvidence
-- is written once on ParentFsynced to AdoptedAndLabeled and preserved into Erased. Letting either be
-- refilled would let a later step substitute a different file for the one this operation made.
CREATE TRIGGER IF NOT EXISTS managed_file_write_intents_guard_update
BEFORE UPDATE ON managed_file_write_intents
BEGIN
    SELECT RAISE(ABORT, 'A managed write intent update never accepts restore-staging authorization.')
    WHERE arcanum_restore_staging_managed_authority_sanitization_authorized() = 1;

    SELECT RAISE(ABORT, 'A managed write intent identity, artifact, label, location, and expected content are immutable.')
    WHERE NEW.WriteOperationId <> OLD.WriteOperationId
        OR NEW.StableEffectIdentityDigest <> OLD.StableEffectIdentityDigest
        OR NEW.ArtifactId <> OLD.ArtifactId
        OR NEW.SensitivityLabelId <> OLD.SensitivityLabelId
        OR NEW.SensitivityLabelDigest <> OLD.SensitivityLabelDigest
        OR NEW.DurableLocationEvidence <> OLD.DurableLocationEvidence
        OR NEW.ExpectedContentHash <> OLD.ExpectedContentHash
        OR NEW.ExpectedContentLength <> OLD.ExpectedContentLength
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'A managed write intent update requires the exact prior revision.')
    WHERE NEW.Revision <> OLD.Revision + 1;

    SELECT RAISE(ABORT, 'A managed write intent follows only one step forward through ParentFsynced, a terminal clean or manual outcome, or adoption to erasure.')
    WHERE NOT (
        (OLD.PhaseCode BETWEEN 1 AND 6 AND NEW.PhaseCode = OLD.PhaseCode + 1)
        OR (OLD.PhaseCode BETWEEN 1 AND 6 AND NEW.PhaseCode IN (8, 9))
        OR (OLD.PhaseCode = 7 AND NEW.PhaseCode = 10)
    );

    SELECT RAISE(ABORT, 'A pending sensitivity label projection is byte-for-byte immutable through ParentFsynced.')
    WHERE NEW.PhaseCode BETWEEN 1 AND 6
        AND NEW.PendingArtifactSensitivityLabel IS NOT OLD.PendingArtifactSensitivityLabel;

    SELECT RAISE(ABORT, 'A created-child physical identity is filled exactly once on Prepared to TempCreated and never changes afterward.')
    WHERE NOT (
        (
            OLD.PhaseCode = 1
            AND NEW.PhaseCode = 2
            AND OLD.CreatedChildPhysicalIdentityDigest IS NULL
            AND NEW.CreatedChildPhysicalIdentityDigest IS NOT NULL
        )
        OR (
            NOT (OLD.PhaseCode = 1 AND NEW.PhaseCode = 2)
            AND NEW.CreatedChildPhysicalIdentityDigest IS OLD.CreatedChildPhysicalIdentityDigest
        )
    );

    SELECT RAISE(ABORT, 'Final ownership evidence is filled exactly once on ParentFsynced to AdoptedAndLabeled and never changes afterward.')
    WHERE NOT (
        (
            OLD.PhaseCode = 6
            AND NEW.PhaseCode = 7
            AND OLD.FinalOwnershipEvidence IS NULL
            AND NEW.FinalOwnershipEvidence IS NOT NULL
        )
        OR (
            NOT (OLD.PhaseCode = 6 AND NEW.PhaseCode = 7)
            AND NEW.FinalOwnershipEvidence IS OLD.FinalOwnershipEvidence
        )
    );

    SELECT RAISE(ABORT, 'Adoption requires the persisted created-child physical identity.')
    WHERE NEW.PhaseCode = 7
        AND NEW.CreatedChildPhysicalIdentityDigest IS NULL;

    SELECT RAISE(ABORT, 'Every managed write phase edge except adoption to erasure requires managed-file intent authorization with both erasure scopes closed.')
    WHERE NEW.PhaseCode <> 10
        AND (
            arcanum_managed_file_intent_mutation_authorized() = 0
            OR arcanum_sensitivity_purge_authorized() = 1
            OR arcanum_covenant_family_maintenance_authorized() = 1
        );

    SELECT RAISE(ABORT, 'The adoption to erasure edge refuses managed-file intent authorization and requires exactly one erasure scope.')
    WHERE NEW.PhaseCode = 10
        AND (
            arcanum_managed_file_intent_mutation_authorized() = 1
            OR (arcanum_sensitivity_purge_authorized() + arcanum_covenant_family_maintenance_authorized()) <> 1
        );

    SELECT RAISE(ABORT, 'The adoption to erasure edge requires its exact DeletionVerified local erasure work item.')
    WHERE NEW.PhaseCode = 10
        AND NOT EXISTS (
            SELECT 1
            FROM local_erasure_work_items
            WHERE SourceWriteOperationId = OLD.WriteOperationId
                AND StateCode = 2
                AND ArtifactId = OLD.ArtifactId
                AND SourceSensitivityLabelId = OLD.SensitivityLabelId
        );

    SELECT RAISE(ABORT, 'The adoption to erasure edge requires the exact sensitivity label to be removed first in the same transaction.')
    WHERE NEW.PhaseCode = 10
        AND EXISTS (
            SELECT 1
            FROM artifact_sensitivity
            WHERE LabelId = OLD.SensitivityLabelId
        );
END;
