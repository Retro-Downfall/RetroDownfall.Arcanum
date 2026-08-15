-- The always-present parent journal of a managed owner deletion. The deletion effect has to be
-- named before the owner row disappears, because after the delete commits there is nothing left to
-- derive an owner from: a crash between the delete and workspace-marker cleanup would otherwise
-- leave an effect no operation can claim. The intent is prepared first, the core delete trigger
-- copies it into the monotonic event, and marker cleanup finishes it. This table is core so that
-- Campaign deletion never depends on an optional capability tier being installed.
CREATE TABLE IF NOT EXISTS owner_deletion_operation_intents (
    OperationId TEXT NOT NULL PRIMARY KEY CHECK (length(OperationId) > 0),
    -- Campaign = 1, Session = 2, matching owner_deletion_events.
    OwnerKindCode INTEGER NOT NULL CHECK (OwnerKindCode IN (1, 2)),
    -- Historical: the owner row is gone by the time later phases run, so there is no foreign key.
    OwnerId TEXT NOT NULL CHECK (length(OwnerId) > 0),
    -- CovenantExclusiveOperation.CampaignDelete. Pinned rather than left open because no other
    -- exclusive operation prepares an owner-deletion intent, and a wrong code here would let a
    -- different operation's gate disposition finalize this journal.
    OperationCode INTEGER NOT NULL CHECK (OperationCode = 2),
    ExclusiveEffectDigest BLOB NOT NULL CHECK (length(ExclusiveEffectDigest) = 32),
    -- Prepared = 1, OwnerDeleted = 2, MarkerCleanupTerminal = 3, Completed = 4.
    PhaseCode INTEGER NOT NULL CHECK (PhaseCode IN (1, 2, 3, 4)),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

-- At most one unfinished intent may claim a historical owner. Two live intents for the same Campaign
-- would let the delete trigger pick an arbitrary one and stamp the wrong effect digest onto the
-- permanent journal. Completed rows are excluded so a later identity reusing that owner is not
-- blocked by finished history.
CREATE UNIQUE INDEX IF NOT EXISTS ux_owner_deletion_operation_intents_active_owner
    ON owner_deletion_operation_intents(OwnerKindCode, OwnerId)
    WHERE PhaseCode <> 4;

-- Startup reconciliation sweeps unfinished intents; the composite finalizer looks up terminal ones.
CREATE INDEX IF NOT EXISTS idx_owner_deletion_operation_intents_phase
    ON owner_deletion_operation_intents(PhaseCode);
