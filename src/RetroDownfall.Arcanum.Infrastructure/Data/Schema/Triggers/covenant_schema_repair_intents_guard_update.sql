-- Recovery cannot infer who owned a schema repair by looking at the repaired catalog, so this row is
-- the whole answer and its identity fields are immutable. The captured dataset generation is included
-- in that: post-commit health evidence records a newly installed generation separately, and letting
-- it overwrite this field would erase what the repair actually started from.
--
-- The graph has one committed path and one proven no-mutation path, and both pass through
-- ReopenPending before any gate disposition, so a terminal phase is only ever reached by the one-shot
-- finalizer after the matching disposition succeeded.
CREATE TRIGGER IF NOT EXISTS covenant_schema_repair_intents_guard_update
BEFORE UPDATE ON covenant_schema_repair_intents
BEGIN
    SELECT RAISE(ABORT, 'A schema repair intent update requires family-maintenance authorization.')
    WHERE arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'A schema repair intent identity, digests, action, tier, captured generation, and authority epoch are immutable.')
    WHERE NEW.OperationId <> OLD.OperationId
        OR NEW.EffectDigest <> OLD.EffectDigest
        OR NEW.InspectedCatalogDigest <> OLD.InspectedCatalogDigest
        OR NEW.RepairActionCode <> OLD.RepairActionCode
        OR NEW.TargetTierCode <> OLD.TargetTierCode
        OR NEW.CapturedDatasetGeneration IS NOT OLD.CapturedDatasetGeneration
        OR NEW.AuthorityEpoch <> OLD.AuthorityEpoch
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'A schema repair intent update requires the exact prior revision.')
    WHERE NEW.Revision <> OLD.Revision + 1;

    SELECT RAISE(ABORT, 'A schema repair intent follows only the committed repair path or the proven no-mutation path.')
    WHERE NOT (
        (OLD.PhaseCode = 1 AND NEW.PhaseCode IN (2, 4))
        OR (OLD.PhaseCode = 2 AND NEW.PhaseCode = 3)
        OR (OLD.PhaseCode = 3 AND NEW.PhaseCode = 4)
        OR (OLD.PhaseCode = 4 AND NEW.PhaseCode IN (5, 6))
    );
END;
