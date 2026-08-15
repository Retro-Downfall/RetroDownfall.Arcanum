-- Every phase advance is a compare-and-swap on durable recovery authority, so this guard holds four
-- things at once. The mutation scope keeps unauthorized writers out. The exact prior phase revision
-- means two racing recovery attempts cannot both believe they moved the intent. The immutable
-- columns are the owner, scope, and evidence a later phase authenticates against: if they could be
-- edited, a caller could substitute a different owner or effect and have recovery finish someone
-- else's operation. The one-time physical-evidence fields cannot be rewritten once observed, because
-- their whole value is that they came from a specific open handle at a specific moment; a rewrite
-- would let a fabricated identity pass the same-handle comparison. A terminal row cannot change at
-- all, so a completed, compensated, or orphaned record stays exactly the evidence it was.
CREATE TRIGGER IF NOT EXISTS campaign_path_marker_intents_guard_update
BEFORE UPDATE ON campaign_path_marker_intents
BEGIN
    SELECT RAISE(ABORT, 'A Campaign path marker intent update requires the marker intent mutation scope.')
    WHERE arcanum_campaign_path_marker_intent_mutation_authorized() = 0;

    SELECT RAISE(ABORT, 'A terminal Campaign path marker intent cannot be changed.')
    WHERE OLD.PhaseCode IN (12, 13, 16);

    SELECT RAISE(ABORT, 'A Campaign path marker intent update requires the exact prior phase revision.')
    WHERE NEW.PhaseRevision <> OLD.PhaseRevision + 1;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its identity.')
    WHERE NEW.IntentId <> OLD.IntentId;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its owner operation.')
    WHERE NEW.OwnerOperationId <> OLD.OwnerOperationId;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its Campaign.')
    WHERE NEW.CampaignId <> OLD.CampaignId;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its kind.')
    WHERE NEW.IntentKindCode <> OLD.IntentKindCode;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its exclusive owner operation.')
    WHERE NEW.ExclusiveOwnerOperationCode IS NOT OLD.ExclusiveOwnerOperationCode;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its owner effect digest.')
    WHERE NEW.OwnerEffectDigest <> OLD.OwnerEffectDigest;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its apply request digest.')
    WHERE NEW.ApplyRequestDigest IS NOT OLD.ApplyRequestDigest;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its marker digest.')
    WHERE NEW.MarkerDigest <> OLD.MarkerDigest;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its target display path.')
    WHERE NEW.TargetDisplayPath <> OLD.TargetDisplayPath;

    SELECT RAISE(ABORT, 'A Campaign path marker intent cannot change its prior path revision.')
    WHERE NEW.PriorRevision <> OLD.PriorRevision;

    SELECT RAISE(ABORT, 'A recorded temporary physical identity cannot be rewritten.')
    WHERE OLD.TemporaryPhysicalIdentityDigest IS NOT NULL
        AND NEW.TemporaryPhysicalIdentityDigest IS NOT OLD.TemporaryPhysicalIdentityDigest;

    SELECT RAISE(ABORT, 'A recorded target observation cannot be rewritten.')
    WHERE OLD.TargetObservationCode IS NOT NULL
        AND NEW.TargetObservationCode IS NOT OLD.TargetObservationCode;

    SELECT RAISE(ABORT, 'A recorded reopened target identity cannot be rewritten.')
    WHERE OLD.ReopenedTargetPhysicalIdentityDigest IS NOT NULL
        AND NEW.ReopenedTargetPhysicalIdentityDigest IS NOT OLD.ReopenedTargetPhysicalIdentityDigest;
END;
