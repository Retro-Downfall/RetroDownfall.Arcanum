-- Retention may remove only an intent whose external work is finished and whose material has already
-- been scrubbed. A nonterminal row is the sole durable record of a filesystem operation in flight,
-- so deleting it would strand a marker no later run could recognize as its own. Orphaned is
-- deliberately excluded from ordinary retention: it is visible remediation evidence, and only an
-- explicitly confirmed takeover that consumes that exact evidence may remove it. ManualBlocker stays
-- for the same reason. The mutation scope is required alongside the cleanup scope so retention
-- cannot borrow an unrelated owner deletion to reach this table.
CREATE TRIGGER IF NOT EXISTS campaign_path_marker_intents_guard_delete
BEFORE DELETE ON campaign_path_marker_intents
BEGIN
    SELECT RAISE(ABORT, 'A Campaign path marker intent delete requires the marker intent mutation scope.')
    WHERE arcanum_campaign_path_marker_intent_mutation_authorized() = 0;

    SELECT RAISE(ABORT, 'A Campaign path marker intent delete requires an authorized cleanup scope.')
    WHERE arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'Only a completed or compensated Campaign path marker intent can be retained away.')
    WHERE OLD.PhaseCode NOT IN (12, 13);
END;
