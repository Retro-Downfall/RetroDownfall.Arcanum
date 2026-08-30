-- A version is the immutable record of one accepted mutation. Its content hashes and mutation
-- digests are the evidence a receipt was already issued against, and later revisions chain to it by
-- PredecessorVersionId. Editing a row in place would leave that evidence describing text that no
-- longer exists. A correction is written as the next revision in the lane.
CREATE TRIGGER IF NOT EXISTS covenant_versions_guard_update
BEFORE UPDATE ON covenant_versions
BEGIN
    SELECT RAISE(ABORT, 'covenant_versions is append-only; existing rows cannot be updated.');
END;
