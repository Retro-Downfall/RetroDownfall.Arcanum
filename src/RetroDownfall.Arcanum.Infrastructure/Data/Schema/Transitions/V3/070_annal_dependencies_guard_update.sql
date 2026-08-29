-- An edge is part of the version that owns it. Repointing one in place would move a dependency without
-- moving the revision that asserted it, and the ordering check would then be validating a claim nobody
-- made.
CREATE TRIGGER IF NOT EXISTS annal_dependencies_guard_update
BEFORE UPDATE ON annal_dependencies
BEGIN
    SELECT RAISE(ABORT, 'annal_dependencies is append-only; existing edges cannot be repointed.');
END;
