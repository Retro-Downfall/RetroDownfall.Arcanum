-- SQLite cannot reach the parent row from a companion CHECK, so the kind-four condition and the
-- path-hint shape that depends on the observation code are enforced here. Both are the difference
-- between evidence that describes a full-reset child and evidence bolted onto some other kind's
-- intent, which recovery would then read as authority it never granted.
CREATE TRIGGER IF NOT EXISTS campaign_path_full_reset_cleanup_evidence_guard_insert
BEFORE INSERT ON campaign_path_full_reset_cleanup_evidence
BEGIN
    SELECT RAISE(ABORT, 'Full reset cleanup evidence requires the marker intent mutation scope.')
    WHERE arcanum_campaign_path_marker_intent_mutation_authorized() = 0;

    SELECT RAISE(ABORT, 'Full reset cleanup evidence requires an existing kind four parent intent.')
    WHERE NOT EXISTS (
        SELECT 1 FROM campaign_path_marker_intents
        WHERE IntentId = NEW.IntentId AND IntentKindCode = 4);

    -- An opened child carries the committed location hint its rehydration re-derives the root from.
    SELECT RAISE(ABORT, 'An opened full reset cleanup child requires a target display path.')
    WHERE NEW.ObservationCode = 1
        AND (SELECT TargetDisplayPath FROM campaign_path_marker_intents
             WHERE IntentId = NEW.IntentId) IS NULL;

    -- A blocked child must not carry one: it has no replacement authority, and a path beside it
    -- would be the one input a later arm could mistake for permission to go looking.
    SELECT RAISE(ABORT, 'A blocked full reset cleanup child must not carry a target display path.')
    WHERE NEW.ObservationCode IN (2, 3)
        AND (SELECT TargetDisplayPath FROM campaign_path_marker_intents
             WHERE IntentId = NEW.IntentId) IS NOT NULL;
END;
