-- The one Annals table that is meant to change, and the only three ways it must not. A head that could
-- move backwards would make a superseded version current again; one that could change its claim or its
-- store would silently relabel a memory's whole history.
CREATE TRIGGER IF NOT EXISTS annal_heads_validate_update
BEFORE UPDATE ON annal_heads
WHEN NEW.CurrentRevision <= OLD.CurrentRevision
    OR NEW.ClaimId <> OLD.ClaimId
    OR NEW.SubjectStoreCode <> OLD.SubjectStoreCode
BEGIN
    SELECT RAISE(ABORT, 'annal_heads may only advance to a higher revision of the same claim.');
END;
